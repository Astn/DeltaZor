using DZ.TestGen;

public class Test020_UDP128_ZeroRun : ITestCase
{
    public int Id => 20;
    public string Name => "UDP128_ZeroRun";
    public int ExpectedDeltaSize => 14;
    public string[] Tags => new[] { "udp", "128b", "rle", "zero-run", "ideal" };
    public string? Description => "128B packet: all bytes identical";

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

    public ReadOnlyMemory<byte> GenerateNext() => GenerateBase();
}