using DZ.TestGen;

public class Test022_UDP256_Sparse : ITestCase
{
    public int Id => 22;
    public string Name => "UDP256_Sparse";
    public int ExpectedDeltaSize => 72;
    public string[] Tags => new[] { "udp", "256b", "sparse", "inet" };
    public string? Description => "256B packet: 64 bytes changed";

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

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[Size];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();
        for (int i = 0; i < 64; i++)
            span[i] = (byte)(i * 13 % 256);
        return _next = buf;
    }
}