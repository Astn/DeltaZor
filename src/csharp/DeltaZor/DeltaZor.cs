namespace DZ;

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System;

/// <summary>
/// High-performance delta compression using RLE-encoded XOR operations.
/// Supports length changes, multiple compression strategies, and zero-allocation APIs.
///
/// Unified Header Format:
/// [output_length:4][compression_type:1][data...][checksum:4]
///
/// Compression Types:
/// 0x00 = RLE Delta (XOR-based with runs)
/// 0x01 = Full Replace (raw data)
///
/// RLE Opcodes Data Layout:
/// 0x00 = Zero Run
///      Format: [opcode:1][count:7bit]
///      Meaning: Run of count bytes that are identical (no change)
///
/// 0x01 = Non-Zero Run  
///      Format: [opcode:1][count:7bit][xor_data:count]
///      Meaning: Run of count bytes with XOR differences, followed by count bytes of XOR data
///
/// 0x02 = Extension
///      Format: [opcode:1][count:7bit][extension_data:count]
///      Meaning: Append count new bytes, followed by count bytes of extension data
///
/// 0x03 = Truncation
///      Format: [opcode:1][new_length:4]
///      Meaning: Set final output length to new_length
/// Note: All counts use 7-bit variable length encoding where the MSB indicates continuation.
/// </summary>
public static class DeltaZor
{
    // Constants for header format
    private const int HeaderSize = sizeof(int) + sizeof(byte); // output_length + compression_type
    private const int ChecksumSize = sizeof(uint);
    private const int MinDeltaSize = HeaderSize + ChecksumSize;

    // Compression type constants
    private const byte CompressionType_RLE = 0x00;
    private const byte CompressionType_FullReplace = 0x01;

    // Unified Opcode Table (as of October 28, 2025)
    // Core: Highest priority, fully implemented.
    // MOTIF: High priority, partial implementation—focus on completion next (allocation-free detection, SIMD patching).
    // Others: Pending features unless noted; opcodes reserved but not yet implemented.
    // All use 7-bit varint for counts where applicable.

    private const byte RLE_ZeroRun = 0x00; // Implemented: [opcode:1][count:7bit] - Copy/no-change run.
    private const byte RLE_NonZeroRun = 0x01; // Implemented: [opcode:1][count:7bit][xor_data:count] - XOR run.

    private const byte
        RLE_Extension = 0x02; // Implemented: [opcode:1][count:7bit][extension_data:count] - Append new bytes.

    private const byte RLE_Truncation = 0x03; // Implemented: [opcode:1][new_length:4] - Trim to length.

    private const byte
        RLE_UniformMotifRepeat = 0x04; // Partial: Chunk-less mask-based uniform repeats; high priority for full impl.

    private const byte
        RLE_VaryingMotifRepeat = 0x05; // Partial: Chunk-less mask-based varying repeats; high priority for full impl.

    private const byte
        RLE_FloatRun = 0x06; // Pending: Specialized for float32 runs; [opcode:1][count:7bit][float_xor_data:count*4].

    private const byte
        RLE_HalfRun =
            0x07; // Pending: Specialized for half-float (16-bit) runs; [opcode:1][count:7bit][half_xor_data:count*2].

    private const byte
        RLE_ChannelRun =
            0x08; // Pending: Channel-optimized runs; [opcode:1][count:7bit][channels:1][mask:1][changed_data:variable].

    private const byte
        RLE_Arithmetic =
            0x09; // Pending: Arithmetic compression; [opcode:1][model_id:1][count:7bit][compressed_data:variable].

    private const byte
        RLE_Planar =
            0x0A; // Pending: Planar (e.g., color channel) compression; [opcode:1][plane_count:1][count:7bit][plane_data:variable].
    // Reserve 0x0B+ for future (e.g., Clamp-Aware, Global Shift).

    private const int MotifProbeCount = 7; // UnitSizes 2-8
    private static readonly int[] MotifUnitSizes = { 4, 8, 2, 3, 5, 6, 7 };

    private static readonly uint[]
        MotifUnitMods = { 0x1, 0x3, 0x7, 0xF, 0x1F, 0x3F, 0x7F }; // 2^n -1 for fast pos % size

    private static readonly float MotifDensityThreshold = 0.7f; // Prune if popcount(mask)/size >= this
    private const float MotifSavingsThreshold = -0.5f; // Emit if not too much overhead
    private const int MotifMinStreak = 2; // Min repeats for emission
    private const int MaxMotifStreak = 50; // Cap to bound stack

    /// <summary>
    /// Configuration options for delta compression behavior.
    /// </summary>
    public class DeltaOptions
    {
        /// <summary>
        /// Minimum compression ratio required to use RLE (1.0 = always use RLE, 0 = never use RLE).
        /// Default is 0.95 (50% size reduction required).
        /// </summary>
        public double CompressionThreshold { get; set; } = 0.95;

        /// <summary>
        /// Whether to include checksum for corruption detection.
        /// </summary>
        public bool EnableChecksum { get; set; } = true;

        /// <summary>
        /// Maximum buffer size for stack allocation (bytes).
        /// </summary>
        public int MaxStackBufferSize { get; set; } = 4096;

        /// <summary>
        /// Memory pool for large allocations. If null, uses shared pool.
        /// </summary>
        public MemoryPool<byte>? MemoryPool { get; set; }

        /// <summary>
        /// Minimum run length to consider SIMD optimization (bytes).
        /// </summary>
        public int SimdMinThreshold { get; set; } = 32;

        /// <summary>
        /// Maximum stack buffer size for temporary SIMD operations (bytes).
        /// </summary>
        public int SimdMaxStackBufferSize { get; set; } = 1024;

        /// <summary>
        /// Whether to enable SIMD optimizations.
        /// </summary>
        public bool UseSIMD { get; set; } = true;

        public bool EnableMotifDetection { get; set; } = true;
        public int MotifMinRunThreshold { get; set; } = 0; // Align with SimdMinThreshold
    }

    /// <summary>
    /// Statistics about delta compression performance.
    /// </summary>
    public readonly struct DeltaStats
    {
        public int OldSize { get; init; }
        public int NewSize { get; init; }
        public int DeltaSize { get; init; }
        public double CompressionRatio => NewSize > 0 ? (double)DeltaSize / NewSize : 0.0;
        public double ChangeDensity { get; init; }
        public string CompressionType { get; init; }
        public bool UsedRLE { get; init; }
        public PatternCounts PatternCounts { get; init; }
    }

    private readonly struct MotifCandidate
    {
        public readonly int unitSize;
        public readonly int repeatLength;
        public readonly int coveredLength;
        public readonly uint mask;
        public readonly bool isUniform;
        public readonly bool isFull;

        public MotifCandidate(int unitSize, int repeatLength, int coveredLength, uint mask, bool isUniform, bool isFull)
        {
            this.unitSize = unitSize;
            this.repeatLength = repeatLength;
            this.coveredLength = coveredLength;
            this.mask = mask;
            this.isUniform = isUniform;
            this.isFull = isFull;
        }
    }

    /// <summary>
    /// Represents a detected channel pattern for optimization.
    /// </summary>
    internal readonly struct ChannelPattern
    {
        public int Channels { get; init; }
        public byte ChannelMask { get; init; }
        public int ChangedChannels { get; init; }
        public double CompressionSavings { get; init; }
        public bool IsBeneficial { get; init; }

        public static ChannelPattern None => new() { IsBeneficial = false };
    }

    /// <summary>
    /// Counts of different opcodes emitted during delta creation.
    /// </summary>
    public record struct PatternCounts
    {
        public int ZeroRunCount { get; set; } // 0x00 (Implemented)
        public int NonZeroRunCount { get; set; } // 0x01 (Implemented)
        public int ExtensionCount { get; set; } // 0x02 (Implemented)
        public int TruncationCount { get; set; } // 0x03 (Implemented)
        public int UniformMotifCount { get; set; } // 0x04 (Partial)
        public int VaryingMotifCount { get; set; } // 0x05 (Partial)
        public float AverageMaskDensity { get; set; } // Avg popcount(mask)/unitSize for motif sparsity

        public int TotalPatternCount => ZeroRunCount + NonZeroRunCount + ExtensionCount + TruncationCount +
                                        ChannelRunCount + UniformMotifCount + VaryingMotifCount;

        // For future specialized pattern detection
        public int FloatPatternCount { get; set; } // 0x06 (Planned)
        public int HalfPatternCount { get; set; } // 0x07 (Planned)
        public int ChannelRunCount { get; set; } // 0x08 (Planned)
    }

    #region SIMD Helpers

    private static bool UseSIMD(DeltaOptions options) =>
        Vector.IsHardwareAccelerated &&
        Environment.Is64BitProcess &&
        options.UseSIMD;

    private static unsafe void WriteXORDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output,
        int start, int length, DeltaOptions options)
    {
        // Vectorized XOR implementation with graceful fallback
        try
        {
            if (length < 16 || !UseSIMD(options))
            {
                // Scalar fallback
                for (int i = 0; i < length; i++)
                {
                    output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
                }

                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i < vectorCount; i++)
            {
                var oldVec = Vector128.Create(oldData.Slice(start + i * 16, 16));
                var newVec = Vector128.Create(newData.Slice(start + i * 16, 16));
                var xorVec = Vector128.Xor<byte>(oldVec, newVec);
                xorVec.StoreUnsafe(ref output[i * 16]);
            }

            // Handle remainder
            for (int i = vectorCount * 16; i < length; i++)
            {
                output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
        {
            // Graceful fallback to scalar implementation
            for (int i = 0; i < length; i++)
            {
                output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
            }
        }
    }

    private static unsafe void ApplyXORDelta(Span<byte> output, ReadOnlySpan<byte> xorData, int pos, int length,
        DeltaOptions options)
    {
        try
        {
            if (length < 16 || !UseSIMD(options))
            {
                // Scalar fallback
                for (int i = 0; i < length; i++)
                {
                    output[pos + i] ^= xorData[i];
                }

                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i < vectorCount; i++)
            {
                ref byte outputRef = ref output[pos + i * 16];
                var outputVec = Vector128.LoadUnsafe(ref outputRef);
                var xorVec = Vector128.Create(xorData.Slice(i * 16, 16));
                var resultVec = Vector128.Xor<byte>(outputVec, xorVec);
                resultVec.StoreUnsafe(ref outputRef);
            }

            // Handle remainder
            for (int i = vectorCount * 16; i < length; i++)
            {
                output[pos + i] ^= xorData[i];
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
        {
            // Graceful fallback to scalar implementation
            for (int i = 0; i < length; i++)
            {
                output[pos + i] ^= xorData[i];
            }
        }
    }

    private static unsafe void VectorCopy(ReadOnlySpan<byte> source, Span<byte> dest, int length, DeltaOptions options)
    {
        try
        {
            if (length < 16 || !UseSIMD(options))
            {
                // Scalar fallback
                source.CopyTo(dest);
                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i < vectorCount; i++)
            {
                var srcVec = Vector128.Create(source.Slice(i * 16, 16));
                srcVec.StoreUnsafe(ref dest[i * 16]);
            }

            // Handle remainder
            source.Slice(vectorCount * 16, remainder).CopyTo(dest.Slice(vectorCount * 16, remainder));
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
        {
            // Graceful fallback to scalar implementation
            source.CopyTo(dest);
        }
    }

    #endregion

    /// <summary>
    /// Result of delta operations with success/failure information.
    /// </summary>
    public readonly struct DeltaResult<T>
    {
        public bool Success { get; init; }
        public T? Value { get; init; }
        public string? Error { get; init; }

        public static DeltaResult<T> Ok(T value) => new() { Success = true, Value = value };
        public static DeltaResult<T> Fail(string error) => new() { Success = false, Error = error };
    }

    #region Public API - Span-based (high performance)

    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, out DeltaStats stats)
    {
        return CreateDelta(oldData, newData, DefaultOptions, out stats);
    }

    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options,
        out DeltaStats stats)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Conservative allocation: Assume full replace + overhead
        int estimatedSize = HeaderSize + (int)(newData.Length * 1.2) + ChecksumSize;
        byte[] buffer = new byte[estimatedSize];

        if (CreateDelta(oldData, newData, buffer.AsSpan(), out int bytesWritten, options, out stats))
        {
            // Success - return correctly sized array
            Array.Resize(ref buffer, bytesWritten);
            return buffer;
        }
        else
        {
            // Buffer too small - resize and retry (rare, as conservative)
            buffer = new byte[bytesWritten];
            CreateDelta(oldData, newData, buffer.AsSpan(), out _, options, out stats);
            return buffer;
        }
    }

    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output,
        out int bytesWritten, out DeltaStats stats)
    {
        return CreateDelta(oldData, newData, output, out bytesWritten, DefaultOptions, out stats);
    }

    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output,
        out int bytesWritten, DeltaOptions options, out DeltaStats stats)
    {
        stats = default;
        ArgumentNullException.ThrowIfNull(options);

        // Quick pre-check: Skip RLE if obviously full-replace (high density, similar lengths) and motifs disabled
        double density = CalculateChangeDensity(oldData, newData);
        int lengthDiff = Math.Abs(oldData.Length - newData.Length);
        bool obviousFullReplace = density > 0.95 && lengthDiff < Math.Max(1, newData.Length / 10) &&
                                  !options.EnableMotifDetection;
        bool useRLE = !obviousFullReplace;

        // Use ArrayBufferWriter: Attempt RLE, fallback to full if worse
        var writer = new ArrayBufferWriter<byte>();
        var patternCounts = default(PatternCounts);
        bool usedRLE = false;

        if (useRLE)
        {
            // Attempt RLE (includes motifs if enabled/small)
            patternCounts = CreateRLEDelta(oldData, newData, writer, options);
            usedRLE = true;
        }
        else
        {
            // Direct full replace
            writer.Write(newData);
        }

        var dataSpan = writer.WrittenSpan;
        // Fallback: If RLE is significantly larger than full replace, switch to full
        if (usedRLE && dataSpan.Length > newData.Length * 1.5)
        {
            // Discard RLE, write full replace
            writer.Clear(); // Reset buffer
            writer.Write(newData);
            dataSpan = writer.WrittenSpan;
            usedRLE = false;
            patternCounts = default; // No patterns for full
        }

        uint checksum = options.EnableChecksum ? Crc32.Compute(dataSpan) : 0;
        var checksumBytes = BitConverter.GetBytes(checksum);
        int dataSize = dataSpan.Length;
        int totalSize = HeaderSize + dataSize + ChecksumSize;

        if (totalSize > output.Length)
        {
            bytesWritten = totalSize;
            return false;
        }

        // Write header
        BitConverter.TryWriteBytes(output, newData.Length);
        output[4] = usedRLE ? CompressionType_RLE : CompressionType_FullReplace;

        // Copy data
        dataSpan.CopyTo(output.Slice(HeaderSize));

        // Copy checksum
        checksumBytes.CopyTo(output.Slice(HeaderSize + dataSize));

        bytesWritten = totalSize;

        // Populate stats
        stats = new DeltaStats
        {
            OldSize = oldData.Length,
            NewSize = newData.Length,
            DeltaSize = totalSize,
            ChangeDensity = density, // Cached
            CompressionType = usedRLE ? "RLE" : "FullReplace",
            UsedRLE = usedRLE,
            PatternCounts = patternCounts
        };

        return true;
    }

    public static DeltaResult<bool> ApplyDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output,
        out DeltaStats stats)
    {
        stats = default;

        // Validate minimum size
        if (delta.Length < MinDeltaSize)
            return DeltaResult<bool>.Fail("Delta too small for valid header");

        // Parse header
        int outputLength = BitConverter.ToInt32(delta);
        byte compressionType = delta[4];

        if (outputLength < 0)
            return DeltaResult<bool>.Fail("Invalid output length");

        if (output.Length < outputLength)
            return DeltaResult<bool>.Fail("Output buffer too small");

        // Checksum is always present in the format
        ReadOnlySpan<byte> dataSpan = delta.Slice(HeaderSize, delta.Length - HeaderSize - ChecksumSize);
        ReadOnlySpan<byte> checksumSpan = delta.Slice(delta.Length - ChecksumSize);
        uint expectedChecksum = BitConverter.ToUInt32(checksumSpan);

        // Only validate checksum if it was computed (non-zero)
        if (expectedChecksum != 0)
        {
            uint actualChecksum = Crc32.Compute(dataSpan);
            if (expectedChecksum != actualChecksum)
                return DeltaResult<bool>.Fail("Checksum validation failed");
        }

        // Apply compression
        bool success;
        switch (compressionType)
        {
            case CompressionType_RLE:
                success = ApplyRLEDelta(oldData, dataSpan, output[..outputLength], DefaultOptions);
                stats = new DeltaStats
                {
                    OldSize = oldData.Length,
                    NewSize = outputLength,
                    DeltaSize = delta.Length,
                    CompressionType = "RLE",
                    UsedRLE = true,
                    ChangeDensity = CalculateChangeDensity(oldData, output[..Math.Min(oldData.Length, outputLength)])
                };
                break;

            case CompressionType_FullReplace:
                VectorCopy(dataSpan, output[..outputLength], outputLength, DefaultOptions);
                success = true;
                stats = new DeltaStats
                {
                    OldSize = oldData.Length,
                    NewSize = outputLength,
                    DeltaSize = delta.Length,
                    CompressionType = "FullReplace",
                    UsedRLE = false,
                    ChangeDensity = 1.0 // All bytes changed
                };
                break;

            default:
                return DeltaResult<bool>.Fail($"Unknown compression type: {compressionType}");
        }

        return success ? DeltaResult<bool>.Ok(true) : DeltaResult<bool>.Fail("Delta application failed");
    }

    public static DeltaStats AnalyzeDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
    {
        int changes = 0;
        int minLength = Math.Min(oldData.Length, newData.Length);

        for (int i = 0; i < minLength; i++)
        {
            if (oldData[i] != newData[i]) changes++;
        }

        // Add length difference as "changes"
        changes += Math.Abs(oldData.Length - newData.Length);

        return new DeltaStats
        {
            OldSize = oldData.Length,
            NewSize = newData.Length,
            ChangeDensity = minLength > 0 ? (double)changes / minLength : 1.0,
            CompressionType = "Analysis",
            UsedRLE = false
        };
    }

    #endregion

    #region Private Implementation

    private static DeltaOptions DefaultOptions => new() { UseSIMD = true, CompressionThreshold = 2.0 };

    private static int EstimateDeltaSize(int oldLength, int newLength, DeltaOptions options)
    {
        // Simple conservative: Assume full replace worst-case
        return HeaderSize + newLength + ChecksumSize;
    }


    private static PatternCounts CreateRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData,
        IBufferWriter<byte> writer, DeltaOptions options)
    {
        var patternCounts = new PatternCounts();
        int minLength = Math.Min(oldData.Length, newData.Length);
        Span<byte> oneByteSpan = stackalloc byte[1];
        Span<byte> tempBuffer = stackalloc byte[options.MaxStackBufferSize];

        bool useFullXor = minLength <= options.MaxStackBufferSize && options.EnableMotifDetection;
        if (useFullXor)
        {
            Span<byte> xorBuffer = stackalloc byte[minLength];
            WriteXORDelta(oldData, newData, xorBuffer, 0, minLength, options);
            patternCounts = EncodeXorWithMotifs(xorBuffer, writer, options, patternCounts);
        }
        else
        {
            // Streaming RLE without motifs for large data
            int pos = 0;
            while (pos < minLength)
            {
                int runStart = pos;
                bool isZeroRun = oldData[pos] == newData[pos];
                while (pos < minLength && (oldData[pos] == newData[pos]) == isZeroRun)
                    pos++;
                int runLen = pos - runStart;
                byte opcode = isZeroRun ? RLE_ZeroRun : RLE_NonZeroRun;
                oneByteSpan[0] = opcode;
                writer.Write(oneByteSpan);
                Write7BitEncodedInt(writer, runLen);
                if (!isZeroRun)
                {
                    Span<byte> xorTemp = tempBuffer[..runLen];
                    WriteXORDelta(oldData, newData, xorTemp, runStart, runLen, options);
                    writer.Write(xorTemp);
                }

                if (isZeroRun) patternCounts.ZeroRunCount++;
                else patternCounts.NonZeroRunCount++;
            }
        }

        // Handle length differences
        if (newData.Length > oldData.Length)
        {
            ReadOnlySpan<byte> extension = newData[oldData.Length..];
            oneByteSpan[0] = RLE_Extension;
            writer.Write(oneByteSpan);
            Write7BitEncodedInt(writer, extension.Length);
            writer.Write(extension);
            patternCounts = patternCounts with { ExtensionCount = patternCounts.ExtensionCount + 1 };
        }
        else if (newData.Length < oldData.Length)
        {
            oneByteSpan[0] = RLE_Truncation;
            writer.Write(oneByteSpan);
            Write7BitEncodedInt(writer, newData.Length);
            patternCounts = patternCounts with { TruncationCount = patternCounts.TruncationCount + 1 };
        }

        return patternCounts;
    }

    private static int EstimateRLESizeForSpan(ReadOnlySpan<byte> span)
    {
        int size = 0;
        int i = 0;
        int len = span.Length;
        while (i < len)
        {
            bool isZero = span[i] == 0;
            int runLen = 1;
            while (i + runLen < len && (span[i + runLen] == 0) == isZero) runLen++;
            size += 1 + Get7BitEncodedSize(runLen);
            if (!isZero) size += runLen;
            i += runLen;
        }

        return size;
    }

    private static bool CheckUniform(ReadOnlySpan<byte> xorData, int start, int unit, uint msk, int reps)
    {
        int popc = BitOperations.PopCount(msk);
        Span<byte> first = stackalloc byte[popc];
        int idx = 0;
        for (int i = 0; i < unit; i++)
        {
            if ((msk & (1u << i)) != 0)
                first[idx++] = xorData[start + i];
        }

        for (int r = 1; r < reps; r++)
        {
            idx = 0;
            for (int i = 0; i < unit; i++)
            {
                if ((msk & (1u << i)) != 0)
                {
                    if (xorData[start + r * unit + i] != first[idx])
                        return false;
                    idx++;
                }
            }
        }

        return true;
    }

    private static MotifCandidate? FindMotifCandidate(ReadOnlySpan<byte> xorData, int startPos, DeltaOptions options)
    {
        int len = xorData.Length - startPos;
        Span<byte> firstUnitBuffer = stackalloc byte[8];
        for (int u = 0; u < MotifProbeCount; u++)
        {
            int unitSize = MotifUnitSizes[u];
            int maxPossibleRepeat = len / unitSize;
            if (maxPossibleRepeat < MotifMinStreak) continue;

            // Compute mask from first unit
            uint mask = 0;
            int pop = 0;
            for (int i = 0; i < unitSize; i++)
            {
                if (xorData[startPos + i] != 0)
                {
                    mask |= (1u << i);
                    pop++;
                }
            }

            if (pop == 0) continue;

            bool isFull = (pop == unitSize);
            float density = pop / (float)unitSize;
            if (!isFull && density >= MotifDensityThreshold) continue; // Prune high density masked mode only

            int repeatLen;
            bool isUniform;

            if (isFull)
            {
                // Full mode: check for uniform repeats

                xorData.Slice(startPos, unitSize).CopyTo(firstUnitBuffer[..unitSize]);
                // // if (density >= MotifDensityThreshold) continue; // Allow emission for full mode high density repeats
                repeatLen = 1;
                for (int r = 1; r < maxPossibleRepeat; r++)
                {
                    if (!xorData.Slice(startPos + r * unitSize, unitSize).SequenceEqual(firstUnitBuffer[..unitSize]))
                        break;
                    repeatLen++;
                }

                repeatLen = Math.Min(repeatLen, MaxMotifStreak);
                isUniform = true;
            }
            else
            {
                // Masked mode: check pattern consistency
                repeatLen = 1;
                for (int r = 1; r < maxPossibleRepeat; r++)
                {
                    bool matches = true;
                    for (int i = 0; i < unitSize; i++)
                    {
                        byte val = xorData[startPos + r * unitSize + i];
                        if ((mask & (1u << i)) != 0)
                        {
                            if (val == 0)
                            {
                                matches = false;
                                break;
                            }
                        }
                        else
                        {
                            if (val != 0)
                            {
                                matches = false;
                                break;
                            }
                        }
                    }

                    if (!matches) break;
                    repeatLen++;
                }

                repeatLen = Math.Min(repeatLen, MaxMotifStreak);
                isUniform = CheckUniform(xorData, startPos, unitSize, mask, repeatLen);
            }

            if (repeatLen > MaxMotifStreak) continue;
            if (repeatLen < MotifMinStreak) continue;

            int covered = repeatLen * unitSize;
            int headerSize = 1 + 1 + Get7BitEncodedSize(repeatLen) + Get7BitEncodedSize(unitSize);
            int changedCount = isFull ? unitSize : pop;
            int dataSize = changedCount * (isUniform ? 1 : repeatLen);
            int motifSize = isFull ? headerSize + dataSize : headerSize + Get7BitEncodedSize((int)mask) + dataSize;

            int rleSize = EstimateRLESizeForSpan(xorData.Slice(startPos, covered));
            float savings = (rleSize - motifSize) / (float)rleSize;

            if (savings > MotifSavingsThreshold)
            {
                return new MotifCandidate(unitSize, repeatLen, covered, isFull ? 0u : mask, isUniform, isFull);
            }
        }

        return null;
    }

    private static PatternCounts EncodeXorWithMotifs(ReadOnlySpan<byte> xorData, IBufferWriter<byte> writer,
        DeltaOptions options, PatternCounts counts)
    {
        int pos = 0;
        Span<byte> oneByteSpan = stackalloc byte[1];
        Span<byte> tempBuffer = stackalloc byte[options.MaxStackBufferSize];
        Span<int> posListBuffer = stackalloc int[32];
        while (pos < xorData.Length)
        {
            var candidate = FindMotifCandidate(xorData, pos, options);
            if (candidate.HasValue)
            {
                var c = candidate.Value;
                bool isUniform = c.isUniform;
                bool isFull = c.isFull;
                uint mask = c.mask;
                int unitSize = c.unitSize;
                int repeatLength = c.repeatLength;
                int changedCount = isFull ? unitSize : BitOperations.PopCount(mask);
                byte opcode = isUniform ? RLE_UniformMotifRepeat : RLE_VaryingMotifRepeat;
                oneByteSpan[0] = opcode;
                writer.Write(oneByteSpan);
                byte flags = (byte)(isFull ? 0x00 : 0x80);
                oneByteSpan[0] = flags;
                writer.Write(oneByteSpan);
                Write7BitEncodedInt(writer, repeatLength);
                Write7BitEncodedInt(writer, unitSize);
                if (!isFull)
                    Write7BitEncodedInt(writer, (int)mask);

                int dataLen = changedCount * (isUniform ? 1 : repeatLength);
                Span<byte> packed = tempBuffer[..dataLen];
                if (isFull)
                {
                    xorData.Slice(pos, dataLen).CopyTo(packed);
                }
                else
                {
                    Span<int> posList = posListBuffer[..changedCount];
                    int pp = 0;
                    for (int ii = 0; ii < unitSize; ii++)
                        if ((mask & (1u << ii)) != 0)
                            posList[pp++] = ii;
                    int cursor = 0;
                    int maxRr = isUniform ? 1 : repeatLength;
                    for (int rr = 0; rr < maxRr; rr++)
                    for (int jj = 0; jj < changedCount; jj++)
                        packed[cursor++] = xorData[pos + rr * unitSize + posList[jj]];
                }

                writer.Write(packed);

                float density = isFull ? 1.0f : (changedCount / (float)unitSize);
                int totalMotif = counts.UniformMotifCount + counts.VaryingMotifCount + 1;
                float newAvg = (counts.AverageMaskDensity * (totalMotif - 1) + density) / totalMotif;
                if (isUniform)
                {
                    counts.UniformMotifCount++;
                    counts.AverageMaskDensity = newAvg;
                }
                else
                {
                    counts.VaryingMotifCount++;
                    counts.AverageMaskDensity = newAvg;
                }

                pos += c.coveredLength;
                continue;
            }

            // Fallback RLE run
            bool isZero = xorData[pos] == 0;
            int runLen = 1;
            while (pos + runLen < xorData.Length && (xorData[pos + runLen] == 0) == isZero) runLen++;
            byte op = isZero ? RLE_ZeroRun : RLE_NonZeroRun;
            oneByteSpan[0] = op;
            writer.Write(oneByteSpan);
            Write7BitEncodedInt(writer, runLen);
            if (!isZero)
            {
                Span<byte> xorTemp = tempBuffer[..runLen];
                xorData.Slice(pos, runLen).CopyTo(xorTemp);
                writer.Write(xorTemp);
            }

            if (isZero) counts.ZeroRunCount++;
            else counts.NonZeroRunCount++;
            pos += runLen;
        }

        return counts;
    }

    private static bool ApplyRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output,
        DeltaOptions options)
    {
        // Copy base data
        int copyLength = Math.Min(oldData.Length, output.Length);
        oldData[..copyLength].CopyTo(output[..copyLength]);

        // Clear any extended portion
        if (output.Length > oldData.Length)
            output[oldData.Length..].Clear();

        // Apply RLE operations
        var reader = new SpanReader(delta);
        int pos = 0; // Start at beginning of overlapping region

        Span<int> posListBuffer = stackalloc int[32];

        while (reader.Remaining > 0)
        {
            if (!reader.TryReadByte(out byte opcode))
                return false;

            switch (opcode)
            {
                case RLE_ZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int zeroCount))
                        return false;
                    pos += zeroCount;
                    if (pos > output.Length) return false;
                    break;

                case RLE_NonZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int nonZeroCount))
                        return false;
                    if (pos + nonZeroCount > output.Length) return false;
                    ReadOnlySpan<byte> xorData = reader.Read(nonZeroCount);
                    ApplyXORDelta(output, xorData, pos, nonZeroCount, options);
                    pos += nonZeroCount;
                    break;

                case RLE_Extension:
                    if (!reader.TryRead7BitEncodedInt(out int extLen))
                        return false;
                    if (pos + extLen > output.Length) return false;
                    ReadOnlySpan<byte> extData = reader.Read(extLen);
                    extData.CopyTo(output.Slice(pos, extLen));
                    pos += extLen;
                    break;

                case RLE_Truncation:
                    if (!reader.TryRead7BitEncodedInt(out int truncLen))
                        return false;
                    // The output is already sized to truncLen from header
                    // No data to copy, just ensure no more reading
                    pos = truncLen;
                    break;

                case RLE_UniformMotifRepeat:
                {
                    if (!reader.TryReadByte(out byte flags)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int repeatLength)) return false;
                    if (repeatLength < MotifMinStreak) return false;
                    if (!reader.TryRead7BitEncodedInt(out int unitSize)) return false;
                    if (unitSize < 1 || unitSize > 32) return false;

                    bool isMasked = (flags & 0x80) != 0;
                    uint mask = 0;
                    int changedCount;
                    if (isMasked)
                    {
                        if (!reader.TryRead7BitEncodedInt(out int maskInt)) return false;
                        mask = (uint)maskInt;
                        changedCount = BitOperations.PopCount(mask);
                    }
                    else
                    {
                        changedCount = unitSize;
                        mask = (1u << unitSize) - 1u;
                    }

                    ReadOnlySpan<byte> uniformXorData = reader.Read(changedCount);
                    if (pos + unitSize * repeatLength > output.Length) return false;

                    Span<int> posList = posListBuffer[..changedCount];
                    int c = 0;
                    for (int i = 0; i < unitSize; i++)
                        if ((mask & (1u << i)) != 0 || !isMasked)
                            posList[c++] = i;

                    for (int r = 0; r < repeatLength; r++)
                    for (int j = 0; j < changedCount; j++)
                        output[pos + r * unitSize + posList[j]] ^= uniformXorData[j];
                    pos += unitSize * repeatLength;
                }
                    break;

                case RLE_VaryingMotifRepeat:
                {
                    if (!reader.TryReadByte(out byte flags)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int repeatLength)) return false;
                    if (repeatLength < MotifMinStreak) return false;
                    if (!reader.TryRead7BitEncodedInt(out int unitSize)) return false;
                    if (unitSize < 1 || unitSize > 32) return false;

                    bool isMasked = (flags & 0x80) != 0;
                    uint mask = 0;
                    int changedCount;
                    if (isMasked)
                    {
                        if (!reader.TryRead7BitEncodedInt(out int maskInt)) return false;
                        mask = (uint)maskInt;
                        changedCount = BitOperations.PopCount(mask);
                    }
                    else
                    {
                        changedCount = unitSize;
                        mask = (1u << unitSize) - 1u;
                    }

                    int totalXorDataSize = changedCount * repeatLength;
                    ReadOnlySpan<byte> allXorData = reader.Read(totalXorDataSize);
                    if (pos + unitSize * repeatLength > output.Length) return false;

                    Span<int> posList = posListBuffer[..changedCount];
                    int c = 0;
                    for (int i = 0; i < unitSize; i++)
                        if ((mask & (1u << i)) != 0 || !isMasked)
                            posList[c++] = i;

                    int dataCursor = 0;
                    for (int r = 0; r < repeatLength; r++)
                    {
                        for (int j = 0; j < changedCount; j++)
                            output[pos + r * unitSize + posList[j]] ^= allXorData[dataCursor + j];
                        dataCursor += changedCount;
                    }

                    pos += unitSize * repeatLength;
                }
                    break;

                default:
                    return false; // Invalid opcode
            }
        }

        return pos == output.Length;
    }

    private static void Write7BitEncodedInt(IBufferWriter<byte> writer, int value)
    {
        Span<byte> oneByteSpan = stackalloc byte[1];
        uint v = (uint)value;
        while (v >= 0x80)
        {
            oneByteSpan[0] = (byte)(v | 0x80);
            writer.Write(oneByteSpan);
            v >>= 7;
        }

        oneByteSpan[0] = (byte)v;
        writer.Write(oneByteSpan);
    }

    private static int Write7BitEncodedInt(Span<byte> span, int value)
    {
        int pos = 0;
        uint v = (uint)value;
        while (v >= 0x80)
        {
            span[pos++] = (byte)(v | 0x80);
            v >>= 7;
        }

        span[pos++] = (byte)v;
        return pos;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }

        writer.Write((byte)v);
    }

    private static int Get7BitEncodedSize(int value)
    {
        if (value < 0x80) return 1;
        if (value < 0x4000) return 2;
        if (value < 0x200000) return 3;
        if (value < 0x10000000) return 4;
        return 5;
    }

    private static double CalculateChangeDensity(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
    {
        int minLength = Math.Min(oldData.Length, newData.Length);
        if (minLength == 0) return 1.0;

        int changes = 0;
        for (int i = 0; i < minLength; i++)
        {
            if (oldData[i] != newData[i]) changes++;
        }

        changes += Math.Abs(oldData.Length - newData.Length);

        return (double)changes / minLength;
    }

    #endregion

    #region Helper Types

    /// <summary>
    /// Efficient reader for spans with 7-bit encoding support.
    /// </summary>
    private ref struct SpanReader
    {
        public ReadOnlySpan<byte> _span;
        public int _position;

        public SpanReader(ReadOnlySpan<byte> span)
        {
            _span = span;
            _position = 0;
        }

        public int Remaining => _span.Length - _position;

        public bool TryReadByte(out byte value)
        {
            if (_position >= _span.Length)
            {
                value = 0;
                return false;
            }

            value = _span[_position++];
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (_position + 4 > _span.Length)
            {
                value = 0;
                return false;
            }

            value = BitConverter.ToInt32(_span[_position..]);
            _position += 4;
            return true;
        }

        public bool TryRead7BitEncodedInt(out int value)
        {
            value = 0;
            int shift = 0;
            byte b;

            do
            {
                if (!TryReadByte(out b))
                    return false;

                value |= (b & 0x7F) << shift;
                shift += 7;

                if (shift > 35) // Prevent overflow
                    return false;
            } while ((b & 0x80) != 0);

            return true;
        }

        public ReadOnlySpan<byte> Read(int length)
        {
            if (_position + length > _span.Length) throw new EndOfStreamException("Insufficient data in delta");
            var result = _span.Slice(_position, length);
            _position += length;
            return result;
        }
    }

    /// <summary>
    /// Simple CRC32 implementation for checksums.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = InitializeTable();

        private static uint[] InitializeTable()
        {
            const uint polynomial = 0xEDB88320;
            var table = new uint[256];

            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
                }

                table[i] = crc;
            }

            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFF;
        }
    }

    #endregion
}