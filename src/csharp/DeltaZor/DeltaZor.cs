namespace DZ;

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System;
using static DeltaUtils;
using static DeltaEncoder;
using static DeltaDecoder;

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
        public bool EnableChecksum { get; set; } = false;

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
        public OpCodeCounts OpCodeCounts { get; init; }
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
    public record struct OpCodeCounts
    {
        public int ZeroRunCount { get; set; } // 0x00 (Implemented)
        public int NonZeroRunCount { get; set; } // 0x01 (Implemented)
        public int ExtensionCount { get; set; } // 0x02 (Implemented)
        public int TruncationCount { get; set; } // 0x03 (Implemented)
        public int UniformMotifCount { get; set; } // 0x04 (Implemented)
        public int VaryingMotifCount { get; set; } // 0x05 (Implemented)
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
        var patternCounts = default(OpCodeCounts);
        bool usedRLE = false;

        if (useRLE)
        {
            // Attempt RLE (includes motifs if enabled/small)
            patternCounts = DeltaEncoder.CreateRLEDelta(oldData, newData, writer, options);
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

        uint checksum = options.EnableChecksum ? XxHash32Wrapper.Compute(newData) : 0;
        var checksumBytes = BitConverter.GetBytes(checksum);
        int dataSize = dataSpan.Length;
        int checksumSize = options.EnableChecksum ? 4 : 0;
        int totalSize = HeaderSize + dataSize + checksumSize;

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
        if (options.EnableChecksum) checksumBytes.CopyTo(output.Slice(HeaderSize + dataSize));

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
            OpCodeCounts = patternCounts
        };

        return true;
    }

    public static DeltaResult<bool> ApplyDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output,
        out DeltaStats stats)
    {
        stats = default;

        // Validate minimum size
        int minSize = HeaderSize; if (delta.Length < minSize)
            return DeltaResult<bool>.Fail("Delta too small for valid header");

        // Parse header
        int outputLength = BitConverter.ToInt32(delta);
        byte compressionType = delta[4];

        if (outputLength < 0)
            return DeltaResult<bool>.Fail("Invalid output length");

        if (output.Length < outputLength)
            return DeltaResult<bool>.Fail("Output buffer too small");

        var options = DefaultOptions;
        int checksumSize = options.EnableChecksum ? 4 : 0;
        ReadOnlySpan<byte> dataSpan = delta.Slice(HeaderSize, delta.Length - HeaderSize - checksumSize);
        uint expectedChecksum = 0;
        if (options.EnableChecksum)
        {
            ReadOnlySpan<byte> checksumSpan = delta.Slice(delta.Length - checksumSize);
            expectedChecksum = BitConverter.ToUInt32(checksumSpan);
        }

        // Apply compression
        bool success;
        switch (compressionType)
        {
            case CompressionType_RLE:
                success = DeltaDecoder.ApplyRLEDelta(oldData, dataSpan, output[..outputLength], DefaultOptions);
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

        if (success && options.EnableChecksum)
        {
            uint actualChecksum = XxHash32Wrapper.Compute(output.Slice(0, outputLength));
            if (actualChecksum != expectedChecksum)
                return DeltaResult<bool>.Fail("Checksum mismatch");
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
    /// Computes XxHash32 checksums.
    /// </summary>
    public static class XxHash32Wrapper
    {
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            return XxHash32.HashToUInt32(data);
        }
    }

    #endregion
}