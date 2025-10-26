using DZ.TestGen;

public class Test019_UDP128_Mixed : ITestCase
{
    public int Id => 19;
    public string Name => "UDP128_Mixed";
    public int ExpectedDeltaSize => 72;
    public string[] Tags => new[] { "udp", "128b", "mixed", "xor" };
    public string? Description => "128B packet: 50% XOR-flipped";

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
        var ticktock = true;
        var skipsize = 16;
        for (int i = 0; i < Size; i++)
        {
            if (ticktock)
            {
                span[i] ^= 0xFF;
                skipsize--;
                if (skipsize == 0)
                    ticktock = false;
            }
            else
            {
                skipsize++; 
                if (skipsize == 16)
                    ticktock = true;
            }
        }
        return _next = buf;
    }
}