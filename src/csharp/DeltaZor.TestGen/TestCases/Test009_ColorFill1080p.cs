using DZ.TestGen;

/// <summary>
/// This test simulates a 200×200 pixel fill operation in the center of a 1920×1080 RGB image,
/// applying R+20, G-40, B+35 with clamping. This triggers RunArithmetic per channel.
/// 
/// Expected: 3 × RunArithmetic opcodes → ~32 bytes
/// </summary>
public class Test009_ColorFill1080p : ITestCase
{
    public int Id => 9;
    public string Name => "Color_RGB_1080p_Fill";
    public int ExpectedDeltaSize => 32;
    public string[] Tags => new[] { "color", "1080p", "planar", "fill", "run-arithmetic" };
    public string? Description => "200×200 center fill: R+10, G-10, B+15 with clamp";

    private const int W = 1920, H = 1080;
    private const int FillX = 760, FillY = 440, FillSize = 200;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[W * H * 3];
        new Random(0xDE17A20).NextBytes(buf);
        for (int i = 0; i < buf.Length; i++)
        {
            // move values toward outside of the first 64 and last 64 values
            // so when we simulate a color fill operation, we don't hit the clamping
            buf[i] = (byte)(Math.Clamp(buf[i] + 128, 0, 255) - 64);
        }
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[baseSpan.Length];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();

        for (int y = FillY; y < FillY + FillSize; y++)
        for (int x = FillX; x < FillX + FillSize; x++)
        {
            int i = (y * W + x) * 3;
            int r = span[i + 0], g = span[i + 1], b = span[i + 2];
            // this shouldn't clamp due to the pre-clamping in GenerateBase()
            span[i + 0] = (byte)Math.Clamp(r + 20, 0, 255);
            span[i + 1] = (byte)Math.Clamp(g - 40, 0, 255);
            span[i + 2] = (byte)Math.Clamp(b + 35, 0, 255);
        }

        return _next = buf;
    }
}

