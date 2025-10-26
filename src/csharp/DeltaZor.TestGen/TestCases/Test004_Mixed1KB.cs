using DZ.TestGen;

public class Test004_Mixed1KB : ITestCase
{
    public int Id => 4;
    public string Name => "Mixed_1KB";
    public int ExpectedDeltaSize => 600;
    public string[] Tags => new[] { "mixed", "1kb", "texture" };
    public string? Description => "50% bytes XOR-flipped";

    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[1024];
        new Random(0xDE17A20).NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[1024];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();
        for (int i = 0; i < 512; i += 2)
            span[i] ^= 0xFF;
        return _next = buf;
    }
}