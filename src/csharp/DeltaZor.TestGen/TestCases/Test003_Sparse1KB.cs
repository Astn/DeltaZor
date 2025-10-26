using DZ.TestGen;

public class Test003_Sparse1KB : ITestCase
{
    public int Id => 3;
    public string Name => "Sparse_1KB";
    public int ExpectedDeltaSize => 120;
    public string[] Tags => new[] { "sparse", "1kb", "entity" };
    public string? Description => "First 102 bytes changed";

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
        for (int i = 0; i < 102; i++)
            span[i] = (byte)(i % 256);
        return _next = buf;
    }
}