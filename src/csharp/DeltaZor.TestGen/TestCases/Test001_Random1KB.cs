using DZ.TestGen;

namespace DZ.TestGen.TestCases;

public class Test001_Random1KB : ITestCase
{
    public int Id => 1;
    public string Name => "Random_1KB";
    public int ExpectedDeltaSize => 1056;
    public string[] Tags => new[] { "random", "1kb" };
    public string? Description => "1KB of random bytes";

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
        var buf = new byte[1024];
        new Random(0xDE17A21).NextBytes(buf);
        return _next = buf;
    }
}