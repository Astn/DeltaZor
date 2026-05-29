using DZ.TestGen;

namespace DZ.TestGen.TestCases;

/// <summary>
/// Near-threshold boundary vector (TASK-0405): the RLE-encoded delta data lands
/// JUST BELOW newLen × 1.5 (the FullReplace fallback threshold), so RLE is kept.
///
/// Construction: 512B seeded-random base; next is base XORed in alternating runs of
/// length mostly-1 / occasionally-2 (twoEveryN=4), with per-byte random nonzero XOR
/// values. The irregular short runs defeat motif coalescing and maximise RLE overhead
/// (op+count per run, +data on nonzero runs), pushing the RLE data length right up to
/// the boundary without crossing it.
///
/// Measured: raw RLE data = 767 bytes ≤ newLen×1.5 (=768) → RLE retained
/// (compression_type byte 0x00). Pairs with Test045 (raw RLE 769 > 768 → FullReplace),
/// the two adjacent seeds bracketing the strict `&gt;` comparison by ±1 byte. Exercises
/// the C#↔Zig fallback boundary in the shared parity corpus so future threshold drift
/// fails the cross-language byte compare.
/// </summary>
public class Test044_ThresholdBoundary_BelowRLE : ITestCase
{
    public int Id => 44;
    public string Name => "ThresholdBoundary_BelowRLE";
    public int ExpectedDeltaSize => 772; // 5 header + 767 RLE data (RLE kept; just below 1.5×)
    public string[] Tags => new[] { "boundary", "threshold", "rle", "512b" };
    public string? Description =>
        "512B: RLE-encoded delta = 767B, just BELOW newLen×1.5 (768) → RLE kept (compression_type 0x00)";

    private const int Size = 512;
    private const uint SeedBase = 0xDE17A30u;
    private const uint SeedRun = 0xDE17A31u;
    private const int TwoEveryN = 4;

    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size];
        new Random(unchecked((int)SeedBase)).NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        _next = ThresholdBoundaryBuilder.BuildNext(GenerateBase().Span, Size, SeedRun, TwoEveryN);
        return _next;
    }
}
