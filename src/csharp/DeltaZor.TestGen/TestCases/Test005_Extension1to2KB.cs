using DZ.TestGen;

public class Test005_Extension1to2KB : ITestCase
{
    public int Id => 5;
    public string Name => "Extension_1to2KB";
    public int ExpectedDeltaSize => 1040;
    public string[] Tags => new[] { "extension", "length-change" };
    public string? Description => "Append 1KB of random data";

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
        var buf = new byte[2048];
        baseSpan.CopyTo(buf);
        new Random(0xDE17A21).NextBytes(buf.AsSpan(1024));
        return _next = buf;
    }
}