namespace DZ;

using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;

/// &lt;summary&gt;
/// Handles delta encoding/compression operations.
/// &lt;/summary&gt;
public static class DeltaEncoder
{
    // Encoder-specific methods will be moved here:
    // - CreateRLEDelta
    // - EncodeXorWithMotifs
    // - FindMotifCandidate
    // - CheckUniform
    // Dependencies: DeltaUtils for SIMD, 7-bit encoding, constants

    private static bool CheckUniform(ReadOnlySpan&lt;byte&gt; xorData, int start, int unit, uint msk, int reps)
    {
        int popc = BitOperations.PopCount(msk);
        Span&lt;byte&gt; first = stackalloc byte[popc];
        int idx = 0;
        for (int i = 0; i &lt; unit; i++)
        {
            if ((msk &amp; (1u &lt;&lt; i)) != 0)
                first[idx++] = xorData[start + i];
        }

        for (int r = 1; r &lt; reps; r++)
        {
            idx = 0;
            for (int i = 0; i &lt; unit; i++)
            {
                if ((msk &amp; (1u &lt;&lt; i)) != 0)
                {
                    if (xorData[start + r * unit + i] != first[idx])
                        return false;
                    idx++;
                }
            }
        }

        return true;
    }
}