using DZ.TestGen;

public class Test006_RLEIdeal_ZeroRuns : ITestCase
{
    public int Id => 6;
    public string Name => "RLEIdeal_ZeroRuns";
    public int ExpectedDeltaSize => 28; // ~5 runs × (1+1) + header + checksum
    public string[] Tags => new[] { "rle", "ideal", "zero-run", "sparse" };
    public string? Description => "1KB buffer: 5 long runs of identical bytes (zero XOR)";

    private const int Size = 1024;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size];
        var rng = new Random(0xDE17A20);

        // Fill with 5 distinct patterns
        FillRun(buf, 0,   200, (byte)rng.Next(256));
        FillRun(buf, 200, 250, (byte)rng.Next(256));
        FillRun(buf, 450, 300, (byte)rng.Next(256));
        FillRun(buf, 750, 150, (byte)rng.Next(256));
        FillRun(buf, 900, 124, (byte)rng.Next(256));

        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        // Identical to base → XOR = 0 everywhere → 5 long ZeroRuns
        return GenerateBase();
    }

    private static void FillRun(byte[] buf, int start, int length, byte value)
    {
        for (int i = 0; i < length; i++)
            buf[start + i] = value;
    }
}