namespace DZ;

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;



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
    private const byte RLE_UniformMotifRepeat = 0x04; // Partial: Uniform repeating patterns; high priority for full impl.
    private const byte RLE_VaryingMotifRepeat = 0x05; // Partial: Varying repeating patterns; high priority for full impl.
    private const byte RLE_FloatRun = 0x06;         // Pending: Specialized for float32 runs; [opcode:1][count:7bit][float_xor_data:count*4].
    private const byte RLE_HalfRun = 0x07;          // Pending: Specialized for half-float (16-bit) runs; [opcode:1][count:7bit][half_xor_data:count*2].
    private const byte RLE_ChannelRun = 0x08;       // Pending: Channel-optimized runs; [opcode:1][count:7bit][channels:1][mask:1][changed_data:variable].
    private const byte RLE_Arithmetic = 0x09;       // Pending: Arithmetic compression; [opcode:1][model_id:1][count:7bit][compressed_data:variable].
    private const byte RLE_Planar = 0x0A;           // Pending: Planar (e.g., color channel) compression; [opcode:1][plane_count:1][count:7bit][plane_data:variable].
// Reserve 0x0B+ for future (e.g., Clamp-Aware, Global Shift).

    /// <summary>
    /// Configuration options for delta compression behavior.
    /// </summary>
    public class DeltaOptions
    {
        /// <summary>
        /// Minimum compression ratio required to use RLE (0.0 = always use RLE, 1.0 = never use RLE).
        /// Default is 0.5 (50% size reduction required).
        /// </summary>
        public double CompressionThreshold { get; set; } = 0.5;

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
    /// Represents a chunk in a motif pattern.
    /// </summary>
    internal readonly struct MotifChunk
    {
        public byte Type { get; init; }  // 0 = Copy (ZeroRun), 1 = XOR (NonZeroRun)
        public int Size { get; init; }   // Number of bytes for this chunk

        public bool Equals(MotifChunk other) => Type == other.Type && Size == other.Size;
        public override int GetHashCode() => HashCode.Combine(Type, Size);
    }

    /// <summary>
    /// Represents an active motif being detected during RLE creation.
    /// </summary>
    internal class ActiveMotif
    {
        public List<MotifChunk> UnitLayout { get; } = new(8);  // Chunks (e.g., 4 for your example)
        public byte[] FirstUnitXorData { get; set; } = Array.Empty<byte>();  // For uniform (e.g., 5 bytes)
        public List<byte[]> AllUnitXorData { get; } = new(32);  // For varying (per-unit XOR bytes; cap at 32 repeats)
        public int Streak { get; set; } = 1;  // Ongoing
        public int StartOffset { get; set; } = -1;
        public int BlockByteSize { get; set; } = 0;
        public bool IsProvisional { get; set; } = true;
        public bool Confirmed { get; set; } = false;
        public bool CanBeUniform { get; set; } = true;  // Updated during verification

        // Append/verify per unit (ongoing)
        public void AppendOrVerifyUnit(MotifChunk[] newChunks, byte[] newXorData)
        {
            if (UnitLayout.Count == 0)
            {
                // First unit
                foreach (var chunk in newChunks) UnitLayout.Add(chunk);
                FirstUnitXorData = newXorData;  // e.g., 5 bytes
                AllUnitXorData.Add(newXorData);
                CanBeUniform = true;
            }
            else
            {
                // Verify layout
                if (UnitLayout.Count != newChunks.Length || !UnitLayout.SequenceEqual(newChunks))
                {
                    throw new InvalidOperationException("Layout mismatch");
                }
                AllUnitXorData.Add(newXorData);  // Always append for varying fallback

                // Check for uniform
                if (CanBeUniform && !newXorData.SequenceEqual(FirstUnitXorData))
                {
                    CanBeUniform = false;  // Switch to varying mode
                }
            }
            Streak++;
            // BlockByteSize will be set by caller
        }

        // Compute for chosen opcode (called on confirmation)
        public byte[] ComputeReplacementBytes(bool useUniform)
        {
            using var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            byte opcode = useUniform ? RLE_UniformMotifRepeat : RLE_VaryingMotifRepeat;
            writer.Write(opcode);
            Write7BitEncodedInt(writer, Streak);  // e.g., 20
            writer.Write((byte)UnitLayout.Count);  // e.g., 4

            // Shared layout: [Type(1)][Size(7-bit)] per chunk
            foreach (var chunk in UnitLayout)
            {
                writer.Write(chunk.Type);
                Write7BitEncodedInt(writer, chunk.Size);
            }

            // Opcode-specific data
            if (useUniform)
            {
                // 0x05: Just first-unit XOR data
                writer.Write(FirstUnitXorData);
            }
            else
            {
                // 0x06: Full sequential XOR data (all units concatenated)
                foreach (var unitData in AllUnitXorData)
                {
                    writer.Write(unitData);  // 5 bytes each x20 = 100
                }
            }

            return ms.ToArray();
        }

        // Choose best (size-based)
        public (bool IsUniform, byte[] Bytes) GetBestReplacement()
        {
            bool useUniform = CanBeUniform;
            // If not uniform, force varying
            if (!CanBeUniform)
            {
                useUniform = false;
            }
            byte[] bytes = ComputeReplacementBytes(useUniform);
            return (useUniform, bytes);
        }
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

    /// <summary>
    /// Creates a delta between old and new data using spans for zero allocations.
    /// </summary>
    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, out DeltaStats stats)
    {
        return CreateDelta(oldData, newData, DefaultOptions, out stats);
    }

    /// <summary>
    /// Creates a delta between old and new data using spans with custom options.
    /// </summary>
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

    /// <summary>
    /// Creates a delta directly into a span buffer.
    /// Returns true if successful, false if buffer too small (bytesWritten contains required size).
    /// </summary>
    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, out DeltaStats stats)
    {
        return CreateDelta(oldData, newData, output, out bytesWritten, DefaultOptions, out stats);
    }

    /// <summary>
    /// Creates a delta directly into a span buffer with custom options.
    /// Returns true if successful, false if buffer too small (bytesWritten contains required size).
    /// </summary>
    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, DeltaOptions options, out DeltaStats stats)
    {
        stats = default;
        ArgumentNullException.ThrowIfNull(options);

        // Use ArrayBufferWriter for efficient writing - write data first, then prefix header
        var writer = new ArrayBufferWriter<byte>();

        // Determine compression strategy
        // todo, this should be an escape hatch for when the RLE delta is too large, if we bail out before we really know
        // then that's a good thing, because we can't use RLE.
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

    /// <summary>
    /// Applies a delta using spans for zero allocations.
    /// </summary>
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

    /// <summary>
    /// Analyzes compression characteristics using spans.
    /// </summary>
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
        int i = 0;

        // Lists to track recent operations for motif detection
        var recentOperations = new List<(byte opcode, int count, byte[] xorData)>();
        var activeMotifs = new List<ActiveMotif>();

        // Process overlapping region with RLE
        while (i < minLength)
        {
            int runStart = i;
            byte xor = (byte)(oldData[i] ^ newData[i]);

            if (xor == 0)
            {
                // Count zero run
                while (i < minLength && (oldData[i] ^ newData[i]) == 0) i++;
                int runLength = i - runStart;
                WriteRLEOpcode(writer, RLE_ZeroRun, runLength);
                patternCounts = patternCounts with { ZeroRunCount = patternCounts.ZeroRunCount + 1 };
                recentOperations.Add((RLE_ZeroRun, runLength, Array.Empty<byte>()));
            }
            else
            {
                // Count non-zero run
                while (i < minLength && (oldData[i] ^ newData[i]) != 0) i++;
                int runLength = i - runStart;
                WriteRLEOpcode(writer, RLE_NonZeroRun, runLength);
                patternCounts = patternCounts with { NonZeroRunCount = patternCounts.NonZeroRunCount + 1 };

                // Allocate temp buffer for XOR computation
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(runLength);
                Span<byte> xorBuffer = owner.Memory.Span[..runLength];

                try
                {
                    // Compute XOR using SIMD
                    WriteXORDelta(oldData, newData, xorBuffer, runStart, runLength, options);
                    writer.Write(xorBuffer);
                    
                    // Add to recent operations for motif detection
                    recentOperations.Add((RLE_NonZeroRun, runLength, xorBuffer.ToArray()));
                }
                finally
                {
                    owner.Dispose();
                }
            }
            
            // Simple motif detection: Check if we have a repeating pattern
            // This is a simplified implementation - in a full implementation,
            // we would use more sophisticated pattern matching
            if (recentOperations.Count >= 4)
            {
                // Check for simple 2-operation repeating pattern
                var op1 = recentOperations[recentOperations.Count - 4];
                var op2 = recentOperations[recentOperations.Count - 3];
                var op3 = recentOperations[recentOperations.Count - 2];
                var op4 = recentOperations[recentOperations.Count - 1];
                
                // Check if op1 matches op3 and op2 matches op4 (simple repeat pattern)
                if (op1.opcode == op3.opcode && op1.count == op3.count &&
                    op2.opcode == op4.opcode && op2.count == op4.count)
                {
                    // Found a potential motif - for now, just note it
                    // In a full implementation, we would replace the operations with a motif opcode
                }
                
                // Keep only the last 8 operations
                if (recentOperations.Count > 8)
                {
                    recentOperations.RemoveAt(0);
                }
            }
        }

        // Handle length differences
        if (newData.Length > oldData.Length)
        {
            // Extension: append remaining bytes
            ReadOnlySpan<byte> extension = newData[oldData.Length..];
            WriteRLEOpcode(writer, RLE_Extension, extension.Length);
            writer.Write(extension);
            patternCounts = patternCounts with { ExtensionCount = patternCounts.ExtensionCount + 1 };
        }
        else if (newData.Length < oldData.Length)
        {
            // Truncation: set new length
            WriteRLEOpcode(writer, RLE_Truncation, newData.Length);
            patternCounts = patternCounts with { TruncationCount = patternCounts.TruncationCount + 1 };
        }

        return patternCounts;
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
                        if (!reader.TryRead7BitEncodedInt(out int repeatLength))
                            return false;
                        if (!reader.TryReadByte(out byte numChunks))
                            return false;
                        
                        // Read layout chunks
                        var layout = new MotifChunk[numChunks];
                        int totalXorSize = 0;
                        for (int c = 0; c < numChunks; c++)
                        {
                            if (!reader.TryReadByte(out byte chunkType))
                                return false;
                            if (!reader.TryRead7BitEncodedInt(out int chunkSize))
                                return false;
                            layout[c] = new MotifChunk { Type = chunkType, Size = chunkSize };
                            if (chunkType == 0x01) // XOR type
                                totalXorSize += chunkSize;
                        }
                        
                        // Read XOR data for uniform motif
                        byte[] uniformXorData = reader.Read(totalXorSize).ToArray();
                        
                        // Apply uniform motif
                        int[] xorOffsets = new int[numChunks];
                        int offset = 0;
                        for (int c = 0; c < numChunks; c++)
                        {
                            if (layout[c].Type == 0x01) // XOR type
                            {
                                xorOffsets[c] = offset;
                                offset += layout[c].Size;
                            }
                        }
                        
                        for (int r = 0; r < repeatLength; r++)
                        {
                            for (int c = 0; c < numChunks; c++)
                            {
                                var chunk = layout[c];
                                if (chunk.Type == 0x00) // Copy (ZeroRun)
                                {
                                    if (pos + chunk.Size > output.Length) return false;
                                    oldData.Slice(pos, chunk.Size).CopyTo(output.Slice(pos, chunk.Size));
                                    pos += chunk.Size;
                                }
                                else if (chunk.Type == 0x01) // XOR (NonZeroRun)
                                {
                                    if (pos + chunk.Size > output.Length) return false;
                                    int xorOffset = xorOffsets[c];
                                    for (int j = 0; j < chunk.Size; j++)
                                    {
                                        output[pos + j] ^= uniformXorData[xorOffset + j];
                                    }
                                    pos += chunk.Size;
                                }
                            }
                        }
                    }
                    break;

                case RLE_VaryingMotifRepeat:  // VaryingMotifRepeat
                    {
                        if (!reader.TryRead7BitEncodedInt(out int repeatLength))
                            return false;
                        if (!reader.TryReadByte(out byte numChunks))
                            return false;
                        
                        // Read layout chunks
                        var layout = new MotifChunk[numChunks];
                        int totalXorSizePerUnit = 0;
                        for (int c = 0; c < numChunks; c++)
                        {
                            if (!reader.TryReadByte(out byte chunkType))
                                return false;
                            if (!reader.TryRead7BitEncodedInt(out int chunkSize))
                                return false;
                            layout[c] = new MotifChunk { Type = chunkType, Size = chunkSize };
                            if (chunkType == 0x01) // XOR type
                                totalXorSizePerUnit += chunkSize;
                        }
                        
                        // Read all XOR data for varying motif
                        int totalXorDataSize = totalXorSizePerUnit * repeatLength;
                        byte[] allXorData = reader.Read(totalXorDataSize).ToArray();
                        
                        // Apply varying motif
                        int[] xorOffsets = new int[numChunks];
                        int offset = 0;
                        for (int c = 0; c < numChunks; c++)
                        {
                            if (layout[c].Type == 0x01) // XOR type
                            {
                                xorOffsets[c] = offset;
                                offset += layout[c].Size;
                            }
                        }
                        
                        int dataCursor = 0;
                        for (int r = 0; r < repeatLength; r++)
                        {
                            for (int c = 0; c < numChunks; c++)
                            {
                                var chunk = layout[c];
                                if (chunk.Type == 0x00) // Copy (ZeroRun)
                                {
                                    if (pos + chunk.Size > output.Length) return false;
                                    oldData.Slice(pos, chunk.Size).CopyTo(output.Slice(pos, chunk.Size));
                                    pos += chunk.Size;
                                }
                                else if (chunk.Type == 0x01) // XOR (NonZeroRun)
                                {
                                    if (pos + chunk.Size > output.Length) return false;
                                    int xorOffset = dataCursor + xorOffsets[c];
                                    for (int j = 0; j < chunk.Size; j++)
                                    {
                                        output[pos + j] ^= allXorData[xorOffset + j];
                                    }
                                    pos += chunk.Size;
                                }
                            }
                            dataCursor += totalXorSizePerUnit;
                        }
                    }
                    break;

                default:
                    return false; // Invalid opcode
            }
        }

        return pos == output.Length;
    }

    // Removed unused WriteHeader - now inline in CreateDelta

    private static void WriteRLEOpcode(IBufferWriter<byte> writer, byte opcode, int count)
    {
        writer.Write(new byte[] { opcode });
        Write7BitEncodedInt(writer, count);
    }

    private static void Write7BitEncodedInt(IBufferWriter<byte> writer, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write(new byte[] { (byte)(v | 0x80) });
            v >>= 7;
        }
        writer.Write(new byte[] { (byte)v });
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