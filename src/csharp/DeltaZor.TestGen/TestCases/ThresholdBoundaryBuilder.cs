using DZ.TestGen;

namespace DZ.TestGen.TestCases;

/// <summary>
/// Deterministic construction shared by the TASK-0405 near-threshold boundary vectors
/// (Test044 / Test045). Produces a "next" buffer from a base by XORing alternating runs
/// whose lengths are mostly 1 and occasionally 2 (controlled by <paramref name="twoEveryN"/>),
/// with per-byte seeded nonzero XOR values. The irregular short runs defeat motif
/// coalescing and maximise RLE overhead, letting the RLE-encoded delta size be tuned to
/// straddle newLen × 1.5. Seeded RNG → byte-reproducible across runs and languages.
/// </summary>
public static class ThresholdBoundaryBuilder
{
    public static byte[] BuildNext(ReadOnlySpan<byte> baseBuf, int size, uint seedRun, int twoEveryN)
    {
        var next = baseBuf.ToArray();
        var rng = new Random(unchecked((int)seedRun));
        int pos = 0;
        bool changed = true; // start with a changed run
        while (pos < size)
        {
            int runLen = (rng.Next(0, twoEveryN) == 0) ? 2 : 1; // mostly 1, sometimes 2
            runLen = Math.Min(runLen, size - pos);
            if (changed)
                for (int i = 0; i < runLen; i++)
                    next[pos + i] = (byte)(baseBuf[pos + i] ^ (byte)rng.Next(1, 256)); // nonzero XOR
            pos += runLen;
            changed = !changed;
        }
        return next;
    }
}
