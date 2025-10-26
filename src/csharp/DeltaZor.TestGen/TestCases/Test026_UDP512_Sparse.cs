using DZ.TestGen;

public class Test026_UDP512_Sparse : ITestCase
{
    public int Id => 26;
    public string Name => "UDP512_Sparse";
    public int ExpectedDeltaSize => 136;
    public string[] Tags => new[] { "udp", "512b", "sparse", "inet" };
    public string? Description => "512B packet: 128 bytes changed";

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
        for (int i = 0; i < 128; i++)
            span[i] = (byte)(i * 17 % 256);
        return _next = buf;
    }
}