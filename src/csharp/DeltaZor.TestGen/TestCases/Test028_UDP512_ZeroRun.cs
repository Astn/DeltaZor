using DZ.TestGen;

public class Test028_UDP512_ZeroRun : ITestCase
{
    public int Id => 28;
    public string Name => "UDP512_ZeroRun";
    public int ExpectedDeltaSize => 14;
    public string[] Tags => new[] { "udp", "512b", "rle", "zero-run", "ideal" };
    public string? Description => "512B packet: all bytes identical";

    private const int Size = 512;
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