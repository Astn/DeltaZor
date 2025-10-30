namespace DZ;

using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;

/// &lt;summary&gt;
/// Handles delta decoding/decompression operations.
/// &lt;/summary&gt;
public static class DeltaDecoder
{
    // Decoder-specific methods will be moved here:
    // - ApplyRLEDelta
    // - Motif application logic
    // Dependencies: DeltaUtils for SpanReader, SIMD, constants

    public static bool ApplyRLEDelta(ReadOnlySpan&lt;byte&gt; oldData, ReadOnlySpan&lt;byte&gt; delta, Span&lt;byte&gt; output,
        DeltaUtils.DeltaOptions options)
    {
        // Copy base data
        int copyLength = Math.Min(oldData.Length, output.Length);
        oldData[..copyLength].CopyTo(output[..copyLength]);

        // Clear any extended portion
        if (output.Length &gt; oldData.Length)
            output[oldData.Length..].Clear();

        // Apply RLE operations
        var reader = new DeltaUtils.SpanReader(delta);
        int pos = 0; // Start at beginning of overlapping region

        Span&lt;int&gt; posListBuffer = stackalloc int[32];

        while (reader.Remaining &gt; 0)
        {
            if (!reader.TryReadByte(out byte opcode))
                return false;

            switch (opcode)
            {
                case DeltaUtils.RLE_ZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int zeroCount))
                        return false;
                    pos += zeroCount;
                    if (pos &gt; output.Length) return false;
                    break;

                case DeltaUtils.RLE_NonZeroRun:
                    if (!reader.TryRead7BitEncodedInt(out int nonZeroCount))
                        return false;
                    if (pos + nonZeroCount &gt; output.Length) return false;
                    ReadOnlySpan&lt;byte&gt; xorData = reader.Read(nonZeroCount);
                    DeltaUtils.ApplyXORDelta(output, xorData, pos, nonZeroCount, options);
                    pos += nonZeroCount;
                    break;

                case DeltaUtils.RLE_Extension:
                    if (!reader.TryRead7BitEncodedInt(out int extLen))
                        return false;
                    if (pos + extLen &gt; output.Length) return false;
                    ReadOnlySpan&lt;byte&gt; extData = reader.Read(extLen);
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
                    if (repeatLength &lt; DeltaUtils.MotifMinStreak) return false;
                    if (!reader.TryRead7BitEncodedInt(out int unitSize)) return false;
                    if (unitSize &lt; 1 || unitSize &gt; 32) return false;

                    bool isMasked = (flags &amp; 0x80) != 0;
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
                        mask = (1u &lt;&lt; unitSize) - 1u;
                    }

                    ReadOnlySpan&lt;byte&gt; uniformXorData = reader.Read(changedCount);
                    if (pos + unitSize * repeatLength &gt; output.Length) return false;

                    Span&lt;int&gt; posList = posListBuffer[..changedCount];
                    int c = 0;
                    for (int i = 0; i &lt; unitSize; i++)
                        if ((mask &amp; (1u &lt;&lt; i)) != 0 || !isMasked)
                            posList[c++] = i;

                    for (int r = 0; r &lt; repeatLength; r++)
                    for (int j = 0; j &lt; changedCount; j++)
                        output[pos + r * unitSize + posList[j]] ^= uniformXorData[j];
                    pos += unitSize * repeatLength;
                }
                    break;

                case DeltaUtils.RLE_VaryingMotifRepeat:
                {
                    if (!reader.TryReadByte(out byte flags)) return false;
                    if (!reader.TryRead7BitEncodedInt(out int repeatLength)) return false;
                    if (repeatLength &lt; DeltaUtils.MotifMinStreak) return false;
                    if (!reader.TryRead7BitEncodedInt(out int unitSize)) return false;
                    if (unitSize &lt; 1 || unitSize &gt; 32) return false;

                    bool isMasked = (flags &amp; 0x80) != 0;
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
                        mask = (1u &lt;&lt; unitSize) - 1u;
                    }

                    int totalXorDataSize = changedCount * repeatLength;
                    ReadOnlySpan&lt;byte&gt; allXorData = reader.Read(totalXorDataSize);
                    if (pos + unitSize * repeatLength &gt; output.Length) return false;

                    Span&lt;int&gt; posList = posListBuffer[..changedCount];
                    int c = 0;
                    for (int i = 0; i &lt; unitSize; i++)
                        if ((mask &amp; (1u &lt;&lt; i)) != 0 || !isMasked)
                            posList[c++] = i;

                    int dataCursor = 0;
                    for (int r = 0; r &lt; repeatLength; r++)
                    {
                        for (int j = 0; j &lt; changedCount; j++)
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
}