using DZ.TestGen;

public class Test007_RLEIdeal_NonZeroRuns : ITestCase
{
    public int Id => 7;
    public string Name => "RLEIdeal_NonZeroRuns";
    public int ExpectedDeltaSize => 380; // 3 runs × (1+1+100) + header
    public string[] Tags => new[] { "rle", "ideal", "nonzero-run", "patch" };
    public string? Description => "1KB buffer: 3 long runs of identical XOR values";

    private const int Size = 1024;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size];
        var rng = new Random(0xDE17A20);
        rng.NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[Size];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();

        // 3 long runs of identical XOR
        ApplyXorRun(span, 0,   300, 0xAA);
        ApplyXorRun(span, 400, 200, 0x55);
        ApplyXorRun(span, 700, 100, 0xFF);

        return _next = buf;
    }

    private static void ApplyXorRun(Span<byte> data, int start, int length, byte xorValue)
    {
        for (int i = 0; i < length; i++)
            data[start + i] ^= xorValue;
    }
}