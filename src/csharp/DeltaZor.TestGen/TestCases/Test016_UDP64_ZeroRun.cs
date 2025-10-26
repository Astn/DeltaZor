using DZ.TestGen;

public class Test016_UDP64_ZeroRun : ITestCase
{
    public int Id => 16;
    public string Name => "UDP64_ZeroRun";
    public int ExpectedDeltaSize => 14;
    public string[] Tags => new[] { "udp", "64b", "rle", "zero-run", "ideal" };
    public string? Description => "64B packet: 2 long unchanged runs";

    private const int Size = 64;
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

    public ReadOnlyMemory<byte> GenerateNext() => GenerateBase(); // All zero XOR
}