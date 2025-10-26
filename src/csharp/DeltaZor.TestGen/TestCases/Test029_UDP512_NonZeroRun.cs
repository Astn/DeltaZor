using DZ.TestGen;

public class Test029_UDP512_NonZeroRun : ITestCase
{
    public int Id => 29;
    public string Name => "UDP512_NonZeroRun";
    public int ExpectedDeltaSize => 262;
    public string[] Tags => new[] { "udp", "512b", "rle", "nonzero-run", "ideal" };
    public string? Description => "512B packet: 256-byte run of -1";

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

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[Size];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();
        for (int i = 128; i < 384; i++)
            span[i] -= 1;
        return _next = buf;
    }
}