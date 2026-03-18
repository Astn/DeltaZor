namespace DZ;

using System.Buffers;
using System;
using static DeltaUtils;
using static DeltaEncoder;
using static DeltaDecoder;

/// <summary>
/// High-performance delta compression using RLE-encoded XOR operations.
/// Supports length changes, multiple compression strategies, and zero-allocation APIs.
///
/// Unified Header Format:
/// [output_length:4][compression_type:1][data...][checksum:4 (optional)]
///
/// compression_type byte: bits 0-6 = compression algorithm, bit 7 = checksum present
///
/// Compression Types (bits 0-6):
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
    /// <summary>
    /// Configuration options for delta compression behavior.
    /// </summary>
    public class DeltaOptions
    {
        /// <summary>
        /// RLE-to-FullReplace fallback threshold: if the RLE-encoded data exceeds
        /// newData.Length × CompressionThreshold, fall back to FullReplace.
        /// Default is 1.5 (RLE must be within 150% of raw size).
        /// Set to 2.0+ to force RLE in tests that verify RLE/motif behavior.
        /// </summary>
        public double CompressionThreshold { get; set; } = 1.5;

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
        if (usedRLE && dataSpan.Length > newData.Length * options.CompressionThreshold)
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

        // Write header — set bit 7 of compression_type if checksum is present (self-describing format)
        BitConverter.TryWriteBytes(output, newData.Length);
        byte compressionTypeByte = usedRLE ? CompressionType_RLE : CompressionType_FullReplace;
        if (options.EnableChecksum) compressionTypeByte |= ChecksumFlag;
        output[4] = compressionTypeByte;

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

        // Self-describing checksum: read flag from bit 7 of compression_type byte
        bool hasChecksum = (compressionType & ChecksumFlag) != 0;
        byte baseCompressionType = (byte)(compressionType & CompressionTypeMask);
        int checksumSize = hasChecksum ? ChecksumSize : 0;
        ReadOnlySpan<byte> dataSpan = delta.Slice(HeaderSize, delta.Length - HeaderSize - checksumSize);
        uint expectedChecksum = 0;
        if (hasChecksum)
        {
            ReadOnlySpan<byte> checksumSpan = delta.Slice(delta.Length - checksumSize);
            expectedChecksum = BitConverter.ToUInt32(checksumSpan);
        }

        // Apply compression
        bool success;
        switch (baseCompressionType)
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
                dataSpan.CopyTo(output[..outputLength]);
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
                return DeltaResult<bool>.Fail($"Unknown compression type: {baseCompressionType}");
        }

        if (success && hasChecksum)
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
}
