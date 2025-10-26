using DZ.TestGen;

public class Test023_UDP256_Mixed : ITestCase
{
    public int Id => 23;
    public string Name => "UDP256_Mixed";
    public int ExpectedDeltaSize => 136;
    public string[] Tags => new[] { "udp", "256b", "mixed", "xor" };
    public string? Description => "256B packet: 50% XOR-flipped";

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