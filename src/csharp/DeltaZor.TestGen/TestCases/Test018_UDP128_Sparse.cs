using DZ.TestGen;

public class Test018_UDP128_Sparse : ITestCase
{
    public int Id => 18;
    public string Name => "UDP128_Sparse";
    public int ExpectedDeltaSize => 40;
    public string[] Tags => new[] { "udp", "128b", "sparse", "inet" };
    public string? Description => "128B packet: 32 bytes changed";

    private const int Size = 128;
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
        for (int i = 0; i < 32; i++)
            span[i] = (byte)(i * 11 % 256);
        return _next = buf;
    }
}