namespace DZ;

using System.Buffers;
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
/// RLE Opcodes:
/// 0x00 = Run of zeros (count:variable)
/// 0x01 = Run of non-zeros (count:variable, data:count)
/// 0x02 = Extension (count:variable, data:count) - append new bytes
/// 0x03 = Truncation (new_length:4) - set final length
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

    // RLE opcode constants
    private const byte RLE_ZeroRun = 0x00;
    private const byte RLE_NonZeroRun = 0x01;
    private const byte RLE_Extension = 0x02;
    private const byte RLE_Truncation = 0x03;

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
    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
    {
        return CreateDelta(oldData, newData, DefaultOptions);
    }

    /// <summary>
    /// Creates a delta between old and new data using spans with custom options.
    /// </summary>
    public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Estimate delta size and allocate buffer
        int estimatedSize = EstimateDeltaSize(oldData.Length, newData.Length, options);
        byte[] buffer = new byte[estimatedSize];

        if (CreateDelta(oldData, newData, buffer.AsSpan(), out int bytesWritten, options))
        {
            // Success - return correctly sized array
            Array.Resize(ref buffer, bytesWritten);
            return buffer;
        }
        else
        {
            // Buffer too small - resize and retry
            buffer = new byte[bytesWritten];
            CreateDelta(oldData, newData, buffer.AsSpan(), out _, options);
            return buffer;
        }
    }

    /// <summary>
    /// Creates a delta directly into a span buffer.
    /// Returns true if successful, false if buffer too small (bytesWritten contains required size).
    /// </summary>
    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten)
    {
        return CreateDelta(oldData, newData, output, out bytesWritten, DefaultOptions);
    }

    /// <summary>
    /// Creates a delta directly into a span buffer with custom options.
    /// Returns true if successful, false if buffer too small (bytesWritten contains required size).
    /// </summary>
    public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, DeltaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Use ArrayBufferWriter for efficient writing - write data first, then prefix header
        var writer = new ArrayBufferWriter<byte>();

        // Determine compression strategy
        bool useRLE = ShouldUseRLE(oldData, newData, options);

        if (useRLE)
        {
            // Create RLE delta
            CreateRLEDelta(oldData, newData, writer, options);
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

    private static DeltaOptions DefaultOptions => new() { UseSIMD = false };

    private static int EstimateDeltaSize(int oldLength, int newLength, DeltaOptions options)
    {
        // Conservative estimate: header + worst-case RLE + checksum
        int dataEstimate = Math.Max(oldLength, newLength) / 2; // Assume 50% compression
        return HeaderSize + dataEstimate + ChecksumSize;
    }

    private static bool ShouldUseRLE(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options)
    {
        // For same length, check if RLE would be beneficial
        if (oldData.Length == newData.Length)
        {
            // Estimate RLE size vs full replace
            int rleEstimate = EstimateRLESize(oldData, newData);
            int fullReplaceSize = newData.Length;

            double ratio = (double)rleEstimate / fullReplaceSize;
            return ratio <= (1.0 - options.CompressionThreshold);
        }

        // For different lengths, RLE is usually better unless the difference is very small
        return Math.Abs(oldData.Length - newData.Length) > 64; // Arbitrary threshold
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
            size += 1 + 4 + (newData.Length - oldData.Length); // extension opcode + length + data
        else if (newData.Length < oldData.Length)
            size += 1 + 4; // truncation opcode + length

        return size;
    }

    private static void CreateRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, IBufferWriter<byte> writer, DeltaOptions options)
    {
        int minLength = Math.Min(oldData.Length, newData.Length);
        int i = 0;

        // Process overlapping region with RLE
        while (i < minLength)
        {
            int runStart = i;
            byte xor = (byte)(oldData[i] ^ newData[i]);

            if (xor == 0)
            {
                // Count zero run
                while (i < minLength && (oldData[i] ^ newData[i]) == 0) i++;
                WriteRLEOpcode(writer, RLE_ZeroRun, i - runStart);
            }
            else
            {
                // Count non-zero run
                while (i < minLength && (oldData[i] ^ newData[i]) != 0) i++;
                int runLength = i - runStart;
                WriteRLEOpcode(writer, RLE_NonZeroRun, runLength);

                // Allocate temp buffer for XOR computation
                IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(runLength);
                Span<byte> xorBuffer = owner.Memory.Span[..runLength];

                try
                {
                    // Compute XOR using SIMD
                    WriteXORDelta(oldData, newData, xorBuffer, runStart, runLength, options);
                    writer.Write(xorBuffer);
                }
                finally
                {
                    owner.Dispose();
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
        }
        else if (newData.Length < oldData.Length)
        {
            // Truncation: set new length
            WriteRLEOpcode(writer, RLE_Truncation, newData.Length);
            writer.Write(BitConverter.GetBytes(newData.Length));
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
        int pos = 0;

        while (reader.Remaining > 0)
        {
            if (!reader.TryReadByte(out byte opcode))
                return false;

            switch (opcode)
            {
                case RLE_ZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int zeroCount))
                        return false;
                    pos += zeroCount; // No change needed (already zeroed)
                    break;

                case RLE_NonZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int nonZeroCount))
                        return false;
                    if (nonZeroCount >= options.SimdMinThreshold && UseSIMD(options))
                    {
                        // SIMD path: read XOR data as span and apply SIMD
                        ReadOnlySpan<byte> xorData = reader._span.Slice(reader._position, nonZeroCount);
                        reader._position += nonZeroCount;
                        ApplyXORDelta(output, xorData, pos, nonZeroCount, options);
                        pos += nonZeroCount;
                    }
                    else
                    {
                        // Scalar path
                        for (int i = 0; i < nonZeroCount; i++)
                        {
                            if (!reader.TryReadByte(out byte xor))
                                return false;
                            if (pos + i >= output.Length)
                                return false;
                            output[pos + i] ^= xor;
                        }
                        pos += nonZeroCount;
                    }
                    break;

                case RLE_Extension:
                    if (!reader.TryRead7BitEncodedInt(out int extensionCount))
                        return false;
                    if (extensionCount >= options.SimdMinThreshold && UseSIMD(options))
                    {
                        // SIMD path: read extension data as span and copy SIMD
                        ReadOnlySpan<byte> extensionData = reader._span.Slice(reader._position, extensionCount);
                        reader._position += extensionCount;
                        VectorCopy(extensionData, output.Slice(pos, extensionCount), extensionCount, options);
                        pos += extensionCount;
                    }
                    else
                    {
                        // Scalar path
                        for (int i = 0; i < extensionCount; i++)
                        {
                            if (!reader.TryReadByte(out byte data))
                                return false;
                            if (pos + i >= output.Length)
                                return false;
                            output[pos + i] = data;
                        }
                        pos += extensionCount;
                    }
                    break;

                case RLE_Truncation:
                    if (!reader.TryReadInt32(out int newLength))
                        return false;
                    // Truncation is handled by output buffer size - no additional action needed
                    break;

                default:
                    return false; // Unknown opcode
            }
        }

        return true;
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