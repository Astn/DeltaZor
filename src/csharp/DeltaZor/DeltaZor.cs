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

    private const byte RLE_ZeroRun = 0x00;          // Implemented: [opcode:1][count:7bit] - Copy/no-change run.
    private const byte RLE_NonZeroRun = 0x01;       // Implemented: [opcode:1][count:7bit][xor_data:count] - XOR run.
    private const byte RLE_Extension = 0x02;        // Implemented: [opcode:1][count:7bit][extension_data:count] - Append new bytes.
    private const byte RLE_Truncation = 0x03;       // Implemented: [opcode:1][new_length:4] - Trim to length.
    private const byte RLE_UniformMotifRepeat = 0x04; // Partial: Chunk-less mask-based uniform repeats; high priority for full impl.
    private const byte RLE_VaryingMotifRepeat = 0x05; // Partial: Chunk-less mask-based varying repeats; high priority for full impl.
    private const byte RLE_FloatRun = 0x06;         // Pending: Specialized for float32 runs; [opcode:1][count:7bit][float_xor_data:count*4].
    private const byte RLE_HalfRun = 0x07;          // Pending: Specialized for half-float (16-bit) runs; [opcode:1][count:7bit][half_xor_data:count*2].
    private const byte RLE_ChannelRun = 0x08;       // Pending: Channel-optimized runs; [opcode:1][count:7bit][channels:1][mask:1][changed_data:variable].
    private const byte RLE_Arithmetic = 0x09;       // Pending: Arithmetic compression; [opcode:1][model_id:1][count:7bit][compressed_data:variable].
    private const byte RLE_Planar = 0x0A;           // Pending: Planar (e.g., color channel) compression; [opcode:1][plane_count:1][count:7bit][plane_data:variable].
    // Reserve 0x0B+ for future (e.g., Clamp-Aware, Global Shift).

    private const int MotifProbeCount = 7;  // UnitSizes 2-8
    private static readonly int[] MotifUnitSizes = {2, 3, 4, 5, 6, 7, 8};
    private static readonly uint[] MotifUnitMods = {0x1, 0x3, 0x7, 0xF, 0x1F, 0x3F, 0x7F};  // 2^n -1 for fast pos % size
    private static readonly float MotifDensityThreshold = 0.5f;  // Prune if popcount(mask)/size >= this
    private const float MotifSavingsThreshold = 0.05f;  // >5% smaller than core RLE
    private const int MotifMinStreak = 2;  // Min repeats for emission
    private const int MaxMotifStreak = 50; // Cap to bound stack

    /// <summary>
    /// Configuration options for delta compression behavior.
    /// </summary>
    public class DeltaOptions
    {
        /// <summary>
        /// Minimum compression ratio required to use RLE (0.0 = always use RLE, 1.0 = never use RLE).
        /// Default is 0.5 (50% size reduction required).
        /// </summary>
        public double CompressionThreshold { get; set; } = 0.05;

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
        public int MotifMinRunThreshold { get; set; } = 32;  // Align with SimdMinThreshold
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

    private unsafe ref struct MotifAccumulator
    {
        public fixed uint RollingMasks[MotifProbeCount];
        public fixed int Streaks[MotifProbeCount];
        public byte BestProbeIdx;
        public bool IsActive;
        public int StartPos;

        public static MotifAccumulator Init()
        {
            MotifAccumulator acc = default;
            unsafe
            {
                for (int i = 0; i < MotifProbeCount; i++)
                {
                    acc.RollingMasks[i] = 0;
                    acc.Streaks[i] = 0;
                }
            }
            acc.BestProbeIdx = 255;
            acc.IsActive = false;
            acc.StartPos = -1;
            return acc;
        }

        public unsafe uint GetMask(int probeIdx)
        {
            fixed (uint* p = RollingMasks)
                return p[probeIdx];
        }

        public unsafe int GetStreak(int probeIdx)
        {
            fixed (int* p = Streaks)
                return p[probeIdx];
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
    public readonly struct PatternCounts
    {
        public int ZeroRunCount { get; init; }        // 0x00 (Implemented)
        public int NonZeroRunCount { get; init; }     // 0x01 (Implemented)
        public int ExtensionCount { get; init; }      // 0x02 (Implemented)
        public int TruncationCount { get; init; }     // 0x03 (Implemented)
        public int UniformMotifCount { get; init; }   // 0x04 (Partial)
        public int VaryingMotifCount { get; init; }   // 0x05 (Partial)
        public float AverageMaskDensity { get; init; } // Avg popcount(mask)/unitSize for motif sparsity
        public int TotalPatternCount => ZeroRunCount + NonZeroRunCount + ExtensionCount + TruncationCount + ChannelRunCount + UniformMotifCount + VaryingMotifCount;

        // For future specialized pattern detection
        public int FloatPatternCount { get; init; }   // 0x06 (Planned)
        public int HalfPatternCount { get; init; }    // 0x07 (Planned)
        public int ChannelRunCount { get; init; }     // 0x08 (Planned)
    }

    #region SIMD Helpers

    private static bool UseSIMD(DeltaOptions options) =>
        Vector.IsHardwareAccelerated &&
        Environment.Is64BitProcess &&
        options.UseSIMD;

    private static unsafe void WriteXORDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, int start, int length, DeltaOptions options)
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

    private static unsafe void ApplyXORDelta(Span<byte> output, ReadOnlySpan<byte> xorData, int pos, int length, DeltaOptions options)
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

    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options, out DeltaStats stats)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Estimate delta size and allocate buffer
        int estimatedSize = EstimateDeltaSize(oldData.Length, newData.Length, options);
        byte[] buffer = new byte[estimatedSize];

        if (CreateDelta(oldData, newData, buffer.AsSpan(), out int bytesWritten, options, out stats))
        {
            // Success - return correctly sized array
            Array.Resize(ref buffer, bytesWritten);
            return buffer;
        }
        else
        {
            // Buffer too small - resize and retry
            buffer = new byte[bytesWritten];
            CreateDelta(oldData, newData, buffer.AsSpan(), out _, options, out stats);
            return buffer;
        }
    }

    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, out DeltaStats stats)
    {
        return CreateDelta(oldData, newData, output, out bytesWritten, DefaultOptions, out stats);
    }

    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, DeltaOptions options, out DeltaStats stats)
    {
        stats = default;
        ArgumentNullException.ThrowIfNull(options);

        // Use ArrayBufferWriter for efficient writing - write data first, then prefix header
        var writer = new ArrayBufferWriter<byte>();

        // Determine compression strategy
        bool useRLE = ShouldUseRLE(oldData, newData, options);
        var patternCounts = default(PatternCounts);

        if (useRLE)
        {
            // Create RLE delta
            patternCounts = CreateRLEDelta(oldData, newData, writer, options);
        }
        else
        {
            // Create full replace
            writer.Write(newData);
        }

        var dataSpan = writer.WrittenSpan;
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
        output[4] = useRLE ? CompressionType_RLE : CompressionType_FullReplace;

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
            ChangeDensity = CalculateChangeDensity(oldData, newData),
            CompressionType = useRLE ? "RLE" : "FullReplace",
            UsedRLE = useRLE,
            PatternCounts = patternCounts
        };

        return true;
    }

    public static DeltaResult<bool> ApplyDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output, out DeltaStats stats)
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

    private static DeltaOptions DefaultOptions => new() { UseSIMD = true, CompressionThreshold = 0.05 };

    private static int EstimateDeltaSize(int oldLength, int newLength, DeltaOptions options)
    {
        // Conservative estimate: header + worst-case RLE + checksum
        int dataEstimate = Math.Max(oldLength, newLength) / 2; // Assume 50% compression
        return HeaderSize + dataEstimate + ChecksumSize;
    }

    private static bool ShouldUseRLE(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options)
    {
        // Always estimate RLE size vs full replace for accurate decision
        int rleEstimate = EstimateRLESize(oldData, newData);
        int fullReplaceSize = newData.Length;

        double ratio = (double)rleEstimate / fullReplaceSize;
        bool useRLE = ratio <= (1.0 - options.CompressionThreshold);

        // Bias toward RLE for very low change density (e.g., identical data)
        if (CalculateChangeDensity(oldData, newData) < 0.05) // <5% changes
            useRLE = true;

        return useRLE;
    }

    private static int EstimateRLESize(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
    {
        int size = 0;
        int i = 0;
        int minLength = Math.Min(oldData.Length, newData.Length);

        while (i < minLength)
        {
            int runStart = i;
            byte xor = (byte)(oldData[i] ^ newData[i]);

            if (xor == 0)
            {
                while (i < minLength && (oldData[i] ^ newData[i]) == 0) i++;
            }
            else
            {
                while (i < minLength && (oldData[i] ^ newData[i]) != 0) i++;
            }

            int runLength = i - runStart;
            size += 1 + Get7BitEncodedSize(runLength); // opcode + count

            if (xor != 0)
                size += runLength; // data bytes
        }

        // Add extension/truncation overhead
        if (newData.Length > oldData.Length)
        {
            int extCount = newData.Length - oldData.Length;
            size += 1 + Get7BitEncodedSize(extCount) + extCount; // extension opcode + 7bit count + data
        }
        else if (newData.Length < oldData.Length)
            size += 1 + Get7BitEncodedSize(newData.Length); // truncation opcode + 7bit length

        return size;
    }

    private static PatternCounts CreateRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, IBufferWriter<byte> writer, DeltaOptions options)
    {
        var patternCounts = new PatternCounts();
        int minLength = Math.Min(oldData.Length, newData.Length);
        int pos = 0;

        MotifAccumulator acc = MotifAccumulator.Init();

        Span<byte> provisional = stackalloc byte[options.MaxStackBufferSize];
        int provisionalWritten = 0;
        Span<byte> oneByteSpan = stackalloc byte[1];
        while (pos < minLength)
        {
            int runStart = pos;
            byte xor = (byte)(oldData[pos] ^ newData[pos]);

            bool isZeroRun = xor == 0;

            int runLen = 1;
            while (pos + runLen < minLength && ((oldData[pos + runLen] ^ newData[pos + runLen]) == 0) == isZeroRun)
                runLen++;

            byte opcode = isZeroRun ? RLE_ZeroRun : RLE_NonZeroRun;
            
            // Lazy motif update
            if (options.EnableMotifDetection)
            {
                UpdateAccumulator(ref acc, pos, runLen, isZeroRun, options);
            }

            // Flush provisional if active false now (pruned)
            if (provisionalWritten > 0 && !acc.IsActive)
            {
                writer.Write(provisional.Slice(0, provisionalWritten));
                provisionalWritten = 0;
            }

            // Check for motif emission
            bool emittedMotif = false;
            if (options.EnableMotifDetection && acc.IsActive && acc.BestProbeIdx != 255)
            {
                int probeIdx = acc.BestProbeIdx;
                uint mask = acc.GetMask(probeIdx);
                int unitSize = MotifUnitSizes[probeIdx];
                int covered = pos + runLen - acc.StartPos;

                if (covered % unitSize == 0)
                {
                    int repeatLength = covered / unitSize;
                    float density = BitOperations.PopCount(mask) / (float)unitSize;

                    if (repeatLength >= MotifMinStreak && density < MotifDensityThreshold && repeatLength <= MaxMotifStreak && EstimateMotifSavings(mask, repeatLength, unitSize, covered) > MotifSavingsThreshold)
                    {
                        WriteMotifOpcode(writer, acc, probeIdx, oldData.Slice(acc.StartPos, covered), newData.Slice(acc.StartPos, covered), options, ref patternCounts);
                        emittedMotif = true;
                        provisionalWritten = 0;
                        pos = acc.StartPos + covered;
                        acc = MotifAccumulator.Init(); // Reset
                        continue; // Skip fallback
                    }
                }
            }

            if (!emittedMotif)
            {
                int opcodeSize = 1 + Get7BitEncodedSize(runLen);
                int dataSize = isZeroRun ? 0 : runLen;
                int totalNeeded = opcodeSize + dataSize;

                if (acc.IsActive)
                {
                    if (provisionalWritten + totalNeeded > options.MaxStackBufferSize)
                    {
                        writer.Write(provisional.Slice(0, provisionalWritten));
                        provisionalWritten = 0;
                        acc = MotifAccumulator.Init(); // Flush and reset if full
                    }

                    provisional[provisionalWritten] = opcode;
                    provisionalWritten++;
                    provisionalWritten += Write7BitEncodedInt(provisional.Slice(provisionalWritten), runLen);
                    if (!isZeroRun)
                    {
                        WriteXORDelta(oldData, newData, provisional.Slice(provisionalWritten), pos, runLen, options);
                        provisionalWritten += runLen;
                    }
                }
                else
                {
                    oneByteSpan[0] = opcode;
                    writer.Write(oneByteSpan);
                    Write7BitEncodedInt(writer, runLen);
                    if (!isZeroRun)
                    {
                        Span<byte> xorTemp = stackalloc byte[runLen];
                        WriteXORDelta(oldData, newData, xorTemp, pos, runLen, options);
                        writer.Write(xorTemp);
                    }
                }
            }

            if (isZeroRun) patternCounts = patternCounts with { ZeroRunCount = patternCounts.ZeroRunCount + 1 };
            else patternCounts = patternCounts with { NonZeroRunCount = patternCounts.NonZeroRunCount + 1 };

            pos += runLen;
        }

        // Flush any remaining provisional
        if (provisionalWritten > 0)
        {
            writer.Write(provisional.Slice(0, provisionalWritten));
            provisionalWritten = 0;
        }

        // Handle length differences
        if (newData.Length > oldData.Length)
        {
            // Extension: append remaining bytes
            ReadOnlySpan<byte> extension = newData[oldData.Length..];
            oneByteSpan[0] = RLE_Extension;
            writer.Write(oneByteSpan);
            Write7BitEncodedInt(writer, extension.Length);
            writer.Write(extension);
            patternCounts = patternCounts with { ExtensionCount = patternCounts.ExtensionCount + 1 };
        }
        else if (newData.Length < oldData.Length)
        {
            // Truncation: set new length
            oneByteSpan[0] = RLE_Truncation;
            writer.Write(oneByteSpan);
            Write7BitEncodedInt(writer, newData.Length);
            patternCounts = patternCounts with { TruncationCount = patternCounts.TruncationCount + 1 };
        }

        return patternCounts;
    }

    private static uint ComputeBitRun(int start, int len, int unitSize)
    {
        if (len == 0) return 0;
        if (len >= unitSize) return (1u << unitSize) - 1u;

        int pos = start % unitSize;
        if (pos + len <= unitSize)
        {
            return ((1u << len) - 1u) << pos;
        }
        else
        {
            int highLen = unitSize - pos;
            uint high = ((1u << highLen) - 1u) << pos;
            int lowLen = len - highLen;
            uint low = (1u << lowLen) - 1u;
            return high | low;
        }
    }

    private static void UpdateAccumulator(ref MotifAccumulator acc, int globalPos, int runLen, bool isZeroRun, DeltaOptions options)
    {
        if (!acc.IsActive)
        {
            acc.StartPos = globalPos;
            acc.IsActive = true;
        }

        if (runLen > options.MaxStackBufferSize)
        {
            acc = MotifAccumulator.Init(); // Reset for very large runs
            return;
        }

        Span<uint> oldMasks = stackalloc uint[MotifProbeCount];
        Span<float> densities = stackalloc float[MotifProbeCount];

        unsafe
        {
            for (int i = 0; i < MotifProbeCount; i++)
            {
                oldMasks[i] = acc.RollingMasks[i];
            }
        }

        for (int i = 0; i < MotifProbeCount; i++)
        {
            int unitSize = MotifUnitSizes[i];
            uint bitrun = ComputeBitRun(globalPos, runLen, unitSize);

            if (isZeroRun)
            {
                if ((oldMasks[i] & bitrun) != 0)
                {
                    // Expected change but zero run -> reset
                    unsafe { acc.Streaks[i] = 0; }
                }
                else
                {
                    // Consistent no change -> increment
                    unsafe { acc.Streaks[i]++; }
                }
            }
            else
            {
                uint newMask = oldMasks[i] | bitrun;
                if (newMask != oldMasks[i])
                {
                    unsafe { acc.Streaks[i] = 1; } // New bits introduced
                }
                else
                {
                    unsafe { acc.Streaks[i]++; }
                }
                unsafe { acc.RollingMasks[i] = newMask; }
            }

            uint currentMask = oldMasks[i]; // Use updated for density
            if (!isZeroRun) currentMask |= bitrun;
            densities[i] = BitOperations.PopCount(currentMask) / (float)unitSize;
            if (densities[i] >= MotifDensityThreshold)
            {
                unsafe { acc.Streaks[i] = 0; } // Prune
            }
        }

        // Update best probe
        byte best = 255;
        float bestScore = 0;
        unsafe
        {
            for (int i = 0; i < MotifProbeCount; i++)
            {
                if (acc.Streaks[i] >= MotifMinStreak)
                {
                    float score = acc.Streaks[i] / (densities[i] + 0.01f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (byte)i;
                    }
                }
            }
        }
        acc.BestProbeIdx = best;

        if (best == 255)
        {
            acc.IsActive = false;
        }
    }

    private static float EstimateMotifSavings(uint mask, int repeatLength, int unitSize, int covered)
    {
        int pop = BitOperations.PopCount(mask);
        // Assume varying for conservative estimate
        int motifSize = 1 + 1 + Get7BitEncodedSize(repeatLength) + Get7BitEncodedSize(unitSize) + Get7BitEncodedSize((int)mask) + (pop * repeatLength);
        int rleSizeApprox = (covered / 10) * (1 + Get7BitEncodedSize(10) + 5); // Rough estimate for RLE ops + data
        return (rleSizeApprox - motifSize) / (float)rleSizeApprox;
    }

    private static void WriteMotifOpcode(IBufferWriter<byte> writer, MotifAccumulator acc, int probeIdx, ReadOnlySpan<byte> oldSlice, ReadOnlySpan<byte> newSlice, DeltaOptions options, ref PatternCounts counts)
    {
        uint mask = acc.GetMask(probeIdx);
        int unitSize = MotifUnitSizes[probeIdx];
        int repeatLength = acc.GetStreak(probeIdx);
        int changedCount = BitOperations.PopCount(mask);

        Span<int> posList = stackalloc int[unitSize];
        int p = 0;
        for (int i = 0; i < unitSize; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                posList[p++] = i;
            }
        }

        Span<byte> packed = stackalloc byte[changedCount * repeatLength];
        int dataCursor = 0;
        for (int r = 0; r < repeatLength; r++)
        {
            for (int c = 0; c < changedCount; c++)
            {
                int localPos = posList[c];
                packed[dataCursor + c] = (byte)(oldSlice[r * unitSize + localPos] ^ newSlice[r * unitSize + localPos]);
            }
            dataCursor += changedCount;
        }

        bool isUniform = true;
        if (repeatLength > 1)
        {
            Span<byte> first = packed.Slice(0, changedCount);
            for (int r = 1; r < repeatLength; r++)
            {
                if (!packed.Slice(r * changedCount, changedCount).SequenceEqual(first))
                {
                    isUniform = false;
                    break;
                }
            }
        }

        byte opcode = isUniform ? RLE_UniformMotifRepeat : RLE_VaryingMotifRepeat;
        Span<byte> oneByteSpan = stackalloc byte[1];
        oneByteSpan[0] = opcode;
        writer.Write(oneByteSpan);
        oneByteSpan[0] = 0x80; // Flags: masked
        writer.Write(oneByteSpan);
        Write7BitEncodedInt(writer, repeatLength);
        Write7BitEncodedInt(writer, unitSize);
        Write7BitEncodedInt(writer, (int)mask);

        int dataSize = isUniform ? changedCount : changedCount * repeatLength;
        writer.Write(packed.Slice(0, dataSize));

        float density = changedCount / (float)unitSize;
        int totalMotifs = counts.UniformMotifCount + counts.VaryingMotifCount + 1;
        float newAvg = (counts.AverageMaskDensity * (totalMotifs - 1) + density) / totalMotifs;

        if (isUniform)
        {
            counts = counts with { UniformMotifCount = counts.UniformMotifCount + 1, AverageMaskDensity = newAvg };
        }
        else
        {
            counts = counts with { VaryingMotifCount = counts.VaryingMotifCount + 1, AverageMaskDensity = newAvg };
        }
    }

    private static bool ApplyRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output, DeltaOptions options)
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

                case RLE_UniformMotifRepeat:  // UniformMotifRepeat
                    {
                        if (!reader.TryReadByte(out byte flags))
                            return false;
                        if (!reader.TryRead7BitEncodedInt(out int repeatLength))
                            return false;
                        if (!reader.TryRead7BitEncodedInt(out int unitSize))
                            return false;

                        bool isMasked = (flags & 0x80) != 0;
                        uint mask = 0;
                        int changedCount;
                        if (isMasked)
                        {
                            if (!reader.TryRead7BitEncodedInt(out int maskInt))
                                return false;
                            mask = (uint)maskInt;
                            changedCount = BitOperations.PopCount(mask);
                        }
                        else
                        {
                            changedCount = unitSize;
                        }

                        // Read XOR data for uniform motif
                        ReadOnlySpan<byte> uniformXorData = reader.Read(changedCount);

                        if (pos + unitSize * repeatLength > output.Length) return false;

                        // Get positions from mask
                        Span<int> posList = stackalloc int[unitSize];
                        int c = 0;
                        for (int i = 0; i < unitSize; i++)
                        {
                            if ((mask & (1u << i)) != 0)
                                posList[c++] = i;
                        }

                        // Apply uniform motif
                        for (int r = 0; r < repeatLength; r++)
                        {
                            for (int j = 0; j < changedCount; j++)
                            {
                                int localPos = posList[j];
                                output[pos + localPos] ^= uniformXorData[j];
                            }
                            pos += unitSize;
                        }
                    }
                    break;

                case RLE_VaryingMotifRepeat:  // VaryingMotifRepeat
                    {
                        if (!reader.TryReadByte(out byte flags))
                            return false;
                        if (!reader.TryRead7BitEncodedInt(out int repeatLength))
                            return false;
                        if (!reader.TryRead7BitEncodedInt(out int unitSize))
                            return false;

                        bool isMasked = (flags & 0x80) != 0;
                        uint mask = 0;
                        int changedCount;
                        if (isMasked)
                        {
                            if (!reader.TryRead7BitEncodedInt(out int maskInt))
                                return false;
                            mask = (uint)maskInt;
                            changedCount = BitOperations.PopCount(mask);
                        }
                        else
                        {
                            changedCount = unitSize;
                        }

                        // Read all XOR data for varying motif
                        int totalXorDataSize = changedCount * repeatLength;
                        ReadOnlySpan<byte> allXorData = reader.Read(totalXorDataSize);

                        if (pos + unitSize * repeatLength > output.Length) return false;

                        // Get positions from mask
                        Span<int> posList = stackalloc int[unitSize];
                        int c = 0;
                        for (int i = 0; i < unitSize; i++)
                        {
                            if ((mask & (1u << i)) != 0)
                                posList[c++] = i;
                        }

                        // Apply varying motif
                        int dataCursor = 0;
                        for (int r = 0; r < repeatLength; r++)
                        {
                            for (int j = 0; j < changedCount; j++)
                            {
                                int localPos = posList[j];
                                output[pos + localPos] ^= allXorData[dataCursor + j];
                            }
                            pos += unitSize;
                            dataCursor += changedCount;
                        }
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
            }
            while ((b & 0x80) != 0);

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
    private static class Crc32
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