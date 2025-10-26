using DZ.TestGen;

public class Test014_UDP64_Sparse : ITestCase
{
    public int Id => 14;
    public string Name => "UDP64_Sparse";
    public int ExpectedDeltaSize => 24;
    public string[] Tags => new[] { "udp", "64b", "sparse", "inet" };
    public string? Description => "64B packet: 16 bytes changed";

    private const int Size = 64;
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
        for (int i = 0; i < 16; i++)
            span[i] = (byte)(i * 7 % 256);
        return _next = buf;
    }
}