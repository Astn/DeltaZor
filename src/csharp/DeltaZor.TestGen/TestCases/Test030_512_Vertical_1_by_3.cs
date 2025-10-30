using DZ.TestGen;

public class Test030_512_Vertical_1_by_3 : ITestCase
{
    public int Id => 30;
    public string Name => "D512_Vertical_1_by_3";
    public int ExpectedDeltaSize => 27; 
    public string[] Tags => new[] { "udp", "512b", "mixed", "xor", "vertical" };
    public string? Description => "512B packet: 25% XOR-flipped vertical";

    private const int Size = 512;
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

        const int changeSize = 1;
        const int staticSize = 3;
        const int interval = changeSize + staticSize;

        for (int i = 0; i < Size; i += interval)
        {
            for (int j = 0; j < changeSize; j++)
            {
                if (i + j < Size)
                {
                    span[i + j] ^= 0xFF;
                }
            }
        }
        return _next = buf;
    }
}