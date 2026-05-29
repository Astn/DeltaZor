using DZ.TestGen;

namespace DZ.TestGen.TestCases;

/// <summary>
/// Near-threshold boundary vector (TASK-0405): the RLE-encoded delta data lands
/// JUST ABOVE newLen × 1.5 (the FullReplace fallback threshold), so the encoder
/// discards RLE and falls back to a raw FullReplace.
///
/// Construction: identical scheme to Test044 (512B seeded-random base; next XORed in
/// alternating mostly-1/occasionally-2 runs, twoEveryN=4), differing only in the run
/// seed (the adjacent seed 0xDE17A32 vs Test044's 0xDE17A31). That one-step change
/// nudges the RLE data length 2 bytes over the boundary.
///
/// Measured: raw RLE data = 769 bytes &gt; newLen×1.5 (=768) → FullReplace fallback fires
/// (compression_type byte 0x01, delta = 5 header + 512 raw = 517B). Pairs with Test044
/// (767 ≤ 768 → RLE) to bracket the strict `&gt;` comparison; one vector each side of
/// the threshold, exercising the C#↔Zig fallback boundary in the shared parity corpus.
/// </summary>
public class Test045_ThresholdBoundary_AboveFullReplace : ITestCase
{
    public int Id => 45;
    public string Name => "ThresholdBoundary_AboveFullReplace";
    public int ExpectedDeltaSize => 517; // 5 header + 512 raw (FullReplace; just above 1.5×)
    public string[] Tags => new[] { "boundary", "threshold", "full-replace", "512b" };
    public string? Description =>
        "512B: RLE-encoded delta = 769B, just ABOVE newLen×1.5 (768) → FullReplace fallback (compression_type 0x01)";

    private const int Size = 512;
    private const uint SeedBase = 0xDE17A30u;
    private const uint SeedRun = 0xDE17A32u;
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
