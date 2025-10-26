using DZ.TestGen;

public class Test024_UDP256_ZeroRun : ITestCase
{
    public int Id => 24;
    public string Name => "UDP256_ZeroRun";
    public int ExpectedDeltaSize => 14;
    public string[] Tags => new[] { "udp", "256b", "rle", "zero-run", "ideal" };
    public string? Description => "256B packet: all bytes identical";

    private const int Size = 256;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size];
        new Random(0xDE17A20).NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext() => GenerateBase();
}