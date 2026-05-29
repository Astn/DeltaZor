namespace DZ;

using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;

/// <summary>
/// Handles delta decoding/decompression operations.
/// </summary>
public static class DeltaDecoder
{
    // Decoder-specific methods will be moved here:
    // - ApplyRLEDelta
    // - Motif application logic
    // Dependencies: DeltaUtils for SpanReader, SIMD, constants

    public static bool ApplyRLEDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> delta, Span<byte> output,
        DeltaZor.DeltaOptions options)
    {
        // Copy base data
        int copyLength = Math.Min(oldData.Length, output.Length);
        oldData[..copyLength].CopyTo(output[..copyLength]);

        // Clear any extended portion
        if (output.Length > oldData.Length)
            output[oldData.Length..].Clear();

        // Apply RLE operations
        var reader = new DeltaUtils.SpanReader(delta);
        int pos = 0; // Start at beginning of overlapping region

        Span<int> posListBuffer = stackalloc int[32];
        Span<byte> stepBuf = stackalloc byte[8]; // arithmetic (0x09) step / planar (0x0A) steps scratch

        while (reader.Remaining > 0)
        {
            if (!reader.TryReadByte(out byte opcode))
                return false;

            switch (opcode)
            {
                case DeltaUtils.RLE_ZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int zeroCount))
                        return false;
                    pos += zeroCount;
                    if (pos > output.Length) return false;
                    break;

                case DeltaUtils.RLE_NonZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int nonZeroCount))
                        return false;
                    if (pos + nonZeroCount > output.Length) return false;
                    ReadOnlySpan<byte> xorData = reader.Read(nonZeroCount);
                    DeltaUtils.ApplyXORDelta(output, xorData, pos, nonZeroCount, options);
                    pos += nonZeroCount;
                    break;

                case DeltaUtils.RLE_Extension:
                    if (!reader.TryRead7BitEncodedInt(out int extLen))
                        return false;
                    if (pos + extLen > output.Length) return false;
                    ReadOnlySpan<byte> extData = reader.Read(extLen);
                    extData.CopyTo(output.Slice(pos, extLen));
                    pos += extLen;
                    break;

                case DeltaUtils.RLE_Truncation:
                    if (!reader.TryRead7BitEncodedInt(out int truncLen))
                        return false;
                    // The output is already sized to truncLen from header
                    // No data to copy, just ensure no more reading
                    pos = truncLen;
                    break;

                case DeltaUtils.RLE_UniformMotifRepeat:
                {
                    if (!reader.TryReadByte(out byte flags)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int repeatLength)) return false;
                    if (repeatLength < DeltaUtils.MotifMinStreak) return false;
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

                case DeltaUtils.RLE_VaryingMotifRepeat:
                {
                    if (!reader.TryReadByte(out byte flags)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int repeatLength)) return false;
                    if (repeatLength < DeltaUtils.MotifMinStreak) return false;
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

                case DeltaUtils.RLE_FloatRun:
                {
                    // FloatRun 0x06: [flags][laneCount:7bit][bitmap: ceil(laneCount/8)][packed: 4*changedLanes]
                    // Mirrors Encoder.cs TryEmitFloatRun (source of truth).
                    if (!reader.TryReadByte(out byte floatFlags)) return false;
                    if (floatFlags != 0x00) return false; // reserved flags must be zero
                    if (!reader.TryRead7BitEncodedInt(out int laneCount)) return false;
                    if (laneCount < 2) return false;
                    const int LaneSize = 4;
                    int span = laneCount * LaneSize;
                    if (pos + span > output.Length) return false;

                    int bitmapBytes = (laneCount + 7) / 8;
                    ReadOnlySpan<byte> bitmap = reader.Read(bitmapBytes);
                    for (int l = 0; l < laneCount; l++)
                    {
                        if ((bitmap[l >> 3] & (1 << (l & 7))) == 0) continue;
                        ReadOnlySpan<byte> laneXor = reader.Read(LaneSize);
                        int baseOff = pos + l * LaneSize;
                        output[baseOff] ^= laneXor[0];
                        output[baseOff + 1] ^= laneXor[1];
                        output[baseOff + 2] ^= laneXor[2];
                        output[baseOff + 3] ^= laneXor[3];
                    }
                    pos += span;
                }
                    break;

                case DeltaUtils.RLE_HalfRun:
                {
                    // HalfRun 0x07: [flags][laneCount:7bit][bitmap: ceil(laneCount/8)][packed: 2*changedLanes]
                    // Mirrors Encoder.cs TryEmitHalfRun (source of truth).
                    if (!reader.TryReadByte(out byte halfFlags)) return false;
                    if (halfFlags != 0x00) return false; // reserved flags must be zero
                    if (!reader.TryRead7BitEncodedInt(out int laneCount)) return false;
                    if (laneCount < 2) return false;
                    const int LaneSize = 2;
                    int span = laneCount * LaneSize;
                    if (pos + span > output.Length) return false;

                    int bitmapBytes = (laneCount + 7) / 8;
                    ReadOnlySpan<byte> bitmap = reader.Read(bitmapBytes);
                    for (int l = 0; l < laneCount; l++)
                    {
                        if ((bitmap[l >> 3] & (1 << (l & 7))) == 0) continue;
                        ReadOnlySpan<byte> laneXor = reader.Read(LaneSize);
                        int baseOff = pos + l * LaneSize;
                        output[baseOff] ^= laneXor[0];
                        output[baseOff + 1] ^= laneXor[1];
                    }
                    pos += span;
                }
                    break;

                case DeltaUtils.RLE_ChannelRun:
                {
                    // ChannelRun 0x08: [flags][stride:1][channelMask: ceil(stride/8)][unitCount:7bit][packed: popcount(mask)*unitCount]
                    // Mirrors Encoder.cs TryEmitChannelRun (source of truth).
                    if (!reader.TryReadByte(out byte channelFlags)) return false;
                    if (channelFlags != 0x00) return false; // reserved flags must be zero
                    if (!reader.TryReadByte(out byte strideByte)) return false;
                    int stride = strideByte;
                    if (stride < 1) return false;
                    int channelMaskBytes = (stride + 7) / 8;
                    ReadOnlySpan<byte> channelMask = reader.Read(channelMaskBytes);
                    if (!reader.TryRead7BitEncodedInt(out int unitCount)) return false;
                    if (unitCount < 2) return false;
                    int span = unitCount * stride;
                    if (pos + span > output.Length) return false;

                    for (int u = 0; u < unitCount; u++)
                    {
                        int baseOff = pos + u * stride;
                        for (int c = 0; c < stride; c++)
                        {
                            if ((channelMask[c >> 3] & (1 << (c & 7))) == 0) continue;
                            if (!reader.TryReadByte(out byte b)) return false;
                            output[baseOff + c] ^= b;
                        }
                    }
                    pos += span;
                }
                    break;

                case DeltaUtils.RLE_Arithmetic:
                {
                    // GlobalArithmetic 0x09: [elemWidth:1][step: elemWidth bytes LE][laneCount:7bit]
                    // Mirrors Encoder.cs TryEmitGlobalArithmetic (source of truth). Adds the step
                    // (mod 2^(8*elemWidth), wraparound) into each LE integer lane of `output`
                    // (pre-filled with old) — additive, NOT XOR.
                    if (!reader.TryReadByte(out byte elemWidth)) return false;
                    if (elemWidth != 1 && elemWidth != 2 && elemWidth != 4 && elemWidth != 8) return false;
                    for (int b = 0; b < elemWidth; b++)
                    {
                        if (!reader.TryReadByte(out byte sb)) return false;
                        stepBuf[b] = sb;
                    }
                    if (!reader.TryRead7BitEncodedInt(out int laneCount)) return false;
                    if (laneCount < 2) return false;
                    int span = laneCount * elemWidth;
                    if (pos + span > output.Length) return false;

                    ulong widthMask = elemWidth == 8 ? ulong.MaxValue : (1UL << (8 * elemWidth)) - 1UL;
                    ulong step = 0;
                    for (int b = 0; b < elemWidth; b++) step |= (ulong)stepBuf[b] << (8 * b);

                    for (int l = 0; l < laneCount; l++)
                    {
                        int baseOff = pos + l * elemWidth;
                        ulong cur = 0;
                        for (int b = 0; b < elemWidth; b++) cur |= (ulong)output[baseOff + b] << (8 * b);
                        ulong res = (cur + step) & widthMask;
                        for (int b = 0; b < elemWidth; b++) output[baseOff + b] = (byte)(res >> (8 * b));
                    }
                    pos += span;
                }
                    break;

                case DeltaUtils.RLE_Planar:
                {
                    // PlanarArithmetic 0x0A: [planeCount:1][steps: planeCount bytes][unitCount:7bit]
                    // Mirrors Encoder.cs TryEmitPlanarArithmetic (source of truth). Adds steps[p]
                    // (mod 256, byte wraparound) into each output byte at offset u*P+p (`output`
                    // pre-filled with old) — additive, NOT XOR.
                    if (!reader.TryReadByte(out byte planeCount)) return false;
                    if (planeCount < 1 || planeCount > 8) return false;
                    Span<byte> steps = stepBuf;
                    for (int c = 0; c < planeCount; c++)
                    {
                        if (!reader.TryReadByte(out byte sc)) return false;
                        steps[c] = sc;
                    }
                    if (!reader.TryRead7BitEncodedInt(out int unitCount)) return false;
                    if (unitCount < 2) return false;
                    int span = unitCount * planeCount;
                    if (pos + span > output.Length) return false;

                    for (int u = 0; u < unitCount; u++)
                    {
                        int baseOff = pos + u * planeCount;
                        for (int c = 0; c < planeCount; c++)
                            output[baseOff + c] = (byte)(output[baseOff + c] + steps[c]);
                    }
                    pos += span;
                }
                    break;

                case DeltaUtils.RLE_RunArithmetic:
                {
                    // RunArithmetic 0x0B: [flags:1][step:1][runLen:7bit]
                    // Mirrors Encoder.cs TryEmitRunArithmetic (source of truth). Per-run/segmented
                    // byte arithmetic. flags bit0: 0=wraparound (out += step mod 256), 1=clamp
                    // (out = clamp((i8)step + out, 0, 255)). Additive, NOT XOR; `output` still holds
                    // old at [pos..] so clamp replays exactly (lossless).
                    if (!reader.TryReadByte(out byte raFlags)) return false;
                    if ((raFlags & 0xFE) != 0) return false; // reserved flag bits must be zero
                    bool clamp = (raFlags & 0x01) != 0;
                    if (!reader.TryReadByte(out byte step)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int runLen)) return false;
                    if (runLen < DeltaUtils.RunArithmeticMinRun) return false;
                    if (pos + runLen > output.Length) return false;

                    if (clamp)
                    {
                        sbyte s = (sbyte)step;
                        for (int k = 0; k < runLen; k++)
                        {
                            int r = output[pos + k] + s;
                            if (r < 0) r = 0; else if (r > 255) r = 255;
                            output[pos + k] = (byte)r;
                        }
                    }
                    else
                    {
                        for (int k = 0; k < runLen; k++)
                            output[pos + k] = (byte)(output[pos + k] + step);
                    }
                    pos += runLen;
                }
                    break;

                default:
                    return false; // Invalid opcode
            }
        }

        return reader.Remaining == 0 && pos <= output.Length;
    }
}