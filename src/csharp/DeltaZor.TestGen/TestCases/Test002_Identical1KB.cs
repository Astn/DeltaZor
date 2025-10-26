using DZ.TestGen;

public class Test002_Identical1KB : ITestCase
{
    public int Id => 2;
    public string Name => "Identical_1KB";
    public int ExpectedDeltaSize => 13;
    public string[] Tags => new[] { "identical", "1kb" };
    public string? Description => "Same data — minimal delta";

    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[1024];
        new Random(0xDE17A20).NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext() => GenerateBase();
}