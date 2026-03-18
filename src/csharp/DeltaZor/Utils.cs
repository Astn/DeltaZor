namespace DZ;

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.Intrinsics;
using System;

/// <summary>
/// Shared utilities, constants, and helper structures for DeltaZor.
/// </summary>
public static class DeltaUtils
{
    // Constants for header format
    internal const int HeaderSize = sizeof(int) + sizeof(byte); // output_length + compression_type
    internal const int ChecksumSize = sizeof(uint);
    internal const int MinDeltaSize = HeaderSize + ChecksumSize;

    // Compression type constants
    internal const byte CompressionType_RLE = 0x00;
    internal const byte CompressionType_FullReplace = 0x01;

    // Checksum self-description constants (bit 7 of compression_type byte)
    internal const byte ChecksumFlag = 0x80;
    internal const byte CompressionTypeMask = 0x7F;

    // Unified Opcode Table (as of October 28, 2025)
    // Core: Highest priority, fully implemented.
    // MOTIF: High priority, partial implementation—focus on completion next (allocation-free detection, SIMD patching).
    // Others: Pending features unless noted; opcodes reserved but not yet implemented.
    // All use 7-bit varint for counts where applicable.

    internal const byte RLE_ZeroRun = 0x00; // Implemented: [opcode:1][count:7bit] - Copy/no-change run.
    internal const byte RLE_NonZeroRun = 0x01; // Implemented: [opcode:1][count:7bit][xor_data:count] - XOR run.

    internal const byte
        RLE_Extension = 0x02; // Implemented: [opcode:1][count:7bit][extension_data:count] - Append new bytes.

    internal const byte RLE_Truncation = 0x03; // Implemented: [opcode:1][new_length:4] - Trim to length.

    internal const byte
        RLE_UniformMotifRepeat = 0x04; // Partial: Chunk-less mask-based uniform repeats; high priority for full impl.

    internal const byte
        RLE_VaryingMotifRepeat = 0x05; // Partial: Chunk-less mask-based varying repeats; high priority for full impl.

    internal const byte
        RLE_FloatRun = 0x06; // Pending: Specialized for float32 runs; [opcode:1][count:7bit][float_xor_data:count*4].

    internal const byte
        RLE_HalfRun =
            0x07; // Pending: Specialized for half-float (16-bit) runs; [opcode:1][count:7bit][half_xor_data:count*2].

    internal const byte
        RLE_ChannelRun =
            0x08; // Pending: Channel-optimized runs; [opcode:1][count:7bit][channels:1][mask:1][changed_data:variable].

    internal const byte
        RLE_Arithmetic =
            0x09; // Pending: Arithmetic compression; [opcode:1][model_id:1][count:7bit][compressed_data:variable].

    internal const byte
        RLE_Planar =
            0x0A; // Pending: Planar (e.g., color channel) compression; [opcode:1][plane_count:1][count:7bit][plane_data:variable].
    // Reserve 0x0B+ for future (e.g., Clamp-Aware, Global Shift).

    internal const int MotifProbeCount = 7; // UnitSizes 2-8
    internal static readonly int[] MotifUnitSizes = { 4, 8, 2, 3, 5, 6, 7 };

    internal static readonly uint[]
        MotifUnitMods = { 0x1, 0x3, 0x7, 0xF, 0x1F, 0x3F, 0x7F }; // 2^n -1 for fast pos % size

    internal static readonly float MotifDensityThreshold = 0.7f; // Prune if popcount(mask)/size >= this
    internal const float MotifSavingsThreshold = -0.5f; // Emit if not too much overhead
    internal const int MotifMinStreak = 2; // Min repeats for emission
    internal const int MaxMotifStreak = 50; // Cap to bound stack





    internal readonly struct MotifCandidate
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



    // Default Options
    public static DeltaZor.DeltaOptions DefaultOptions => new() { UseSIMD = true };

    #region SIMD Helpers

    internal static bool UseSIMD(DeltaZor.DeltaOptions options) =>
        Vector.IsHardwareAccelerated &&
        Environment.Is64BitProcess &&
        options.UseSIMD;

    internal static unsafe void WriteXORDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output,
        int start, int length, DeltaZor.DeltaOptions options)
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

    internal static unsafe void ApplyXORDelta(Span<byte> output, ReadOnlySpan<byte> xorData, int pos, int length,
        DeltaZor.DeltaOptions options)
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

    internal static unsafe void VectorCopy(ReadOnlySpan<byte> source, Span<byte> dest, int length, DeltaZor.DeltaOptions options)
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

    internal static void Write7BitEncodedInt(IBufferWriter<byte> writer, int value)
    {
        Span<byte> oneByteSpan = stackalloc byte[1];
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            oneByteSpan[0] = b;
            writer.Write(oneByteSpan);
        } while (v != 0);
    }

    internal static int Write7BitEncodedInt(Span<byte> span, int value)
    {
        int pos = 0;
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            span[pos++] = b;
        } while (v != 0);
        return pos;
    }

    internal static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            writer.Write(b);
        } while (v != 0);
    }

    internal static int Get7BitEncodedSize(int value)
    {
        if (value < 0x80) return 1;
        if (value < 0x4000) return 2;
        if (value < 0x200000) return 3;
        if (value < 0x10000000) return 4;
        return 5;
    }

    internal static int EstimateDeltaSize(int oldLength, int newLength, DeltaZor.DeltaOptions options)
    {
        // Simple conservative: Assume full replace worst-case
        return HeaderSize + newLength + ChecksumSize;
    }

    internal static double CalculateChangeDensity(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData)
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

    internal static int EstimateRLESizeForSpan(ReadOnlySpan<byte> span)
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

    /// <summary>
    /// Efficient reader for spans with 7-bit encoding support.
    /// </summary>
    internal ref struct SpanReader
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
}