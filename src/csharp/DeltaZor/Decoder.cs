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

                default:
                    return false; // Invalid opcode
            }
        }

        return reader.Remaining == 0 && pos <= output.Length;
    }
}