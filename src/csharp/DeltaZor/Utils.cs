namespace DZ;

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.Intrinsics;
using System;

/// &lt;summary&gt;
/// Shared utilities, constants, and helper structures for DeltaZor.
/// &lt;/summary&gt;
public static class DeltaUtils
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

    /// &lt;summary&gt;
    /// Configuration options for delta compression behavior.
    /// &lt;/summary&gt;
    public class DeltaOptions
    {
        /// &lt;summary&gt;
        /// Minimum compression ratio required to use RLE (1.0 = always use RLE, 0 = never use RLE).
        /// Default is 0.95 (50% size reduction required).
        /// &lt;/summary&gt;
        public double CompressionThreshold { get; set; } = 0.95;

        /// &lt;summary&gt;
        /// Whether to include checksum for corruption detection.
        /// &lt;/summary&gt;
        public bool EnableChecksum { get; set; } = true;

        /// &lt;summary&gt;
        /// Maximum buffer size for stack allocation (bytes).
        /// &lt;/summary&gt;
        public int MaxStackBufferSize { get; set; } = 4096;

        /// &lt;summary&gt;
        /// Memory pool for large allocations. If null, uses shared pool.
        /// &lt;/summary&gt;
        public MemoryPool&lt;byte&gt;? MemoryPool { get; set; }

        /// &lt;summary&gt;
        /// Minimum run length to consider SIMD optimization (bytes).
        /// &lt;/summary&gt;
        public int SimdMinThreshold { get; set; } = 32;

        /// &lt;summary&gt;
        /// Maximum stack buffer size for temporary SIMD operations (bytes).
        /// &lt;/summary&gt;
        public int SimdMaxStackBufferSize { get; set; } = 1024;

        /// &lt;summary&gt;
        /// Whether to enable SIMD optimizations.
        /// &lt;/summary&gt;
        public bool UseSIMD { get; set; } = true;

        public bool EnableMotifDetection { get; set; } = true;
        public int MotifMinRunThreshold { get; set; } = 0; // Align with SimdMinThreshold
    }

    /// &lt;summary&gt;
    /// Statistics about delta compression performance.
    /// &lt;/summary&gt;
    public readonly struct DeltaStats
    {
        public int OldSize { get; init; }
        public int NewSize { get; init; }
        public int DeltaSize { get; init; }
        public double CompressionRatio =&gt; NewSize &gt; 0 ? (double)DeltaSize / NewSize : 0.0;
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

    /// &lt;summary&gt;
    /// Represents a detected channel pattern for optimization.
    /// &lt;/summary&gt;
    internal readonly struct ChannelPattern
    {
        public int Channels { get; init; }
        public byte ChannelMask { get; init; }
        public int ChangedChannels { get; init; }
        public double CompressionSavings { get; init; }
        public bool IsBeneficial { get; init; }

        public static ChannelPattern None =&gt; new() { IsBeneficial = false };
    }

    /// &lt;summary&gt;
    /// Counts of different opcodes emitted during delta creation.
    /// &lt;/summary&gt;
    public record struct OpCodeCounts
    {
        public int ZeroRunCount { get; set; } // 0x00 (Implemented)
        public int NonZeroRunCount { get; set; } // 0x01 (Implemented)
        public int ExtensionCount { get; set; } // 0x02 (Implemented)
        public int TruncationCount { get; set; } // 0x03 (Implemented)
        public int UniformMotifCount { get; set; } // 0x04 (Implemented)
        public int VaryingMotifCount { get; set; } // 0x05 (Implemented)
        public float AverageMaskDensity { get; set; } // Avg popcount(mask)/unitSize for motif sparsity

        public int TotalPatternCount =&gt; ZeroRunCount + NonZeroRunCount + ExtensionCount + TruncationCount +
                                        ChannelRunCount + UniformMotifCount + VaryingMotifCount;

        // For future specialized pattern detection
        public int FloatPatternCount { get; set; } // 0x06 (Planned)
        public int HalfPatternCount { get; set; } // 0x07 (Planned)
        public int ChannelRunCount { get; set; } // 0x08 (Planned)
    }

    // Default Options
    public static DeltaOptions DefaultOptions =&gt; new() { UseSIMD = true, CompressionThreshold = 2.0 };

    #region SIMD Helpers

    private static bool UseSIMD(DeltaOptions options) =&gt;
        Vector.IsHardwareAccelerated &amp;&amp;
        Environment.Is64BitProcess &amp;&amp;
        options.UseSIMD;

    private static unsafe void WriteXORDelta(ReadOnlySpan&lt;byte&gt; oldData, ReadOnlySpan&lt;byte&gt; newData, Span&lt;byte&gt; output,
        int start, int length, DeltaOptions options)
    {
        // Vectorized XOR implementation with graceful fallback
        try
        {
            if (length &lt; 16 || !UseSIMD(options))
            {
                // Scalar fallback
                for (int i = 0; i &lt; length; i++)
                {
                    output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
                }

                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i &lt; vectorCount; i++)
            {
                var oldVec = Vector128.Create(oldData.Slice(start + i * 16, 16));
                var newVec = Vector128.Create(newData.Slice(start + i * 16, 16));
                var xorVec = Vector128.Xor&lt;byte&gt;(oldVec, newVec);
                xorVec.StoreUnsafe(ref output[i * 16]);
            }

            // Handle remainder
            for (int i = vectorCount * 16; i &lt; length; i++)
            {
                output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
        {
            // Graceful fallback to scalar implementation
            for (int i = 0; i &lt; length; i++)
            {
                output[i] = (byte)(oldData[start + i] ^ newData[start + i]);
            }
        }
    }

    private static unsafe void ApplyXORDelta(Span&lt;byte&gt; output, ReadOnlySpan&lt;byte&gt; xorData, int pos, int length,
        DeltaOptions options)
    {
        try
        {
            if (length &lt; 16 || !UseSIMD(options))
            {
                // Scalar fallback
                for (int i = 0; i &lt; length; i++)
                {
                    output[pos + i] ^= xorData[i];
                }

                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i &lt; vectorCount; i++)
            {
                ref byte outputRef = ref output[pos + i * 16];
                var outputVec = Vector128.LoadUnsafe(ref outputRef);
                var xorVec = Vector128.Create(xorData.Slice(i * 16, 16));
                var resultVec = Vector128.Xor&lt;byte&gt;(outputVec, xorVec);
                resultVec.StoreUnsafe(ref outputRef);
            }

            // Handle remainder
            for (int i = vectorCount * 16; i &lt; length; i++)
            {
                output[pos + i] ^= xorData[i];
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is PlatformNotSupportedException)
        {
            // Graceful fallback to scalar implementation
            for (int i = 0; i &lt; length; i++)
            {
                output[pos + i] ^= xorData[i];
            }
        }
    }

    private static unsafe void VectorCopy(ReadOnlySpan&lt;byte&gt; source, Span&lt;byte&gt; dest, int length, DeltaOptions options)
    {
        try
        {
            if (length &lt; 16 || !UseSIMD(options))
            {
                // Scalar fallback
                source.CopyTo(dest);
                return;
            }

            // SIMD implementation
            int vectorCount = length / 16;
            int remainder = length % 16;
            for (int i = 0; i &lt; vectorCount; i++)
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

    private static void Write7BitEncodedInt(IBufferWriter&lt;byte&gt; writer, int value)
    {
        Span&lt;byte&gt; oneByteSpan = stackalloc byte[1];
        uint v = (uint)value;
        while (v &gt;= 0x80)
        {
            oneByteSpan[0] = (byte)(v | 0x80);
            writer.Write(oneByteSpan);
            v &gt;&gt;= 7;
        }

        oneByteSpan[0] = (byte)v;
        writer.Write(oneByteSpan);
    }

    private static int Write7BitEncodedInt(Span&lt;byte&gt; span, int value)
    {
        int pos = 0;
        uint v = (uint)value;
        while (v &gt;= 0x80)
        {
            span[pos++] = (byte)(v | 0x80);
            v &gt;&gt;= 7;
        }

        span[pos++] = (byte)v;
        return pos;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v &gt;= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v &gt;&gt;= 7;
        }

        writer.Write((byte)v);
    }

    private static int Get7BitEncodedSize(int value)
    {
        if (value &lt; 0x80) return 1;
        if (value &lt; 0x4000) return 2;
        if (value &lt; 0x200000) return 3;
        if (value &lt; 0x10000000) return 4;
        return 5;
    }

    private static int EstimateDeltaSize(int oldLength, int newLength, DeltaOptions options)
    {
        // Simple conservative: Assume full replace worst-case
        return HeaderSize + newLength + ChecksumSize;
    }

    private static double CalculateChangeDensity(ReadOnlySpan&lt;byte&gt; oldData, ReadOnlySpan&lt;byte&gt; newData)
    {
        int minLength = Math.Min(oldData.Length, newData.Length);
        if (minLength == 0) return 1.0;

        int changes = 0;
        for (int i = 0; i &lt; minLength; i++)
        {
            if (oldData[i] != newData[i]) changes++;
        }

        changes += Math.Abs(oldData.Length - newData.Length);

        return (double)changes / minLength;
    }

    private static int EstimateRLESizeForSpan(ReadOnlySpan&lt;byte&gt; span)
    {
        int size = 0;
        int i = 0;
        int len = span.Length;
        while (i &lt; len)
        {
            bool isZero = span[i] == 0;
            int runLen = 1;
            while (i + runLen &lt; len &amp;&amp; (span[i + runLen] == 0) == isZero) runLen++;
            size += 1 + Get7BitEncodedSize(runLen);
            if (!isZero) size += runLen;
            i += runLen;
        }

        return size;
    }

    /// &lt;summary&gt;
    /// Simple CRC32 implementation for checksums.
    /// &lt;/summary&gt;
    public static class Crc32
    {
        private static readonly uint[] Table = InitializeTable();

        private static uint[] InitializeTable()
        {
            const uint polynomial = 0xEDB88320;
            var table = new uint[256];

            for (uint i = 0; i &lt; 256; i++)
            {
                uint crc = i;
                for (int j = 0; j &lt; 8; j++)
                {
                    crc = (crc &amp; 1) != 0 ? (crc &gt;&gt; 1) ^ polynomial : crc &gt;&gt; 1;
                }

                table[i] = crc;
            }

            return table;
        }

        public static uint Compute(ReadOnlySpan&lt;byte&gt; data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) &amp; 0xFF] ^ (crc &gt;&gt; 8);
            }

            return crc ^ 0xFFFFFFFF;
        }
    }

    /// &lt;summary&gt;
    /// Efficient reader for spans with 7-bit encoding support.
    /// &lt;/summary&gt;
    private ref struct SpanReader
    {
        public ReadOnlySpan&lt;byte&gt; _span;
        public int _position;

        public SpanReader(ReadOnlySpan&lt;byte&gt; span)
        {
            _span = span;
            _position = 0;
        }

        public int Remaining =&gt; _span.Length - _position;

        public bool TryReadByte(out byte value)
        {
            if (_position &gt;= _span.Length)
            {
                value = 0;
                return false;
            }

            value = _span[_position++];
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (_position + 4 &gt; _span.Length)
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

                value |= (b &amp; 0x7F) &lt;&lt; shift;
                shift += 7;

                if (shift &gt; 35) // Prevent overflow
                    return false;
            } while ((b &amp; 0x80) != 0);

            return true;
        }

        public ReadOnlySpan&lt;byte&gt; Read(int length)
        {
            if (_position + length &gt; _span.Length) throw new EndOfStreamException("Insufficient data in delta");
            var result = _span.Slice(_position, length);
            _position += length;
            return result;
        }
    }
}