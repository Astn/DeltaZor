using System.Runtime.InteropServices;
using DZ.TestGen;

/// <summary>
/// Exercises the FloatRun (0x06) opcode: a 256×3 float32 buffer (3072 bytes, below the
/// 4096 full-XOR threshold so it takes the motif/full-XOR encode path) where only the
/// 3rd float channel is incremented by +0.5f. The changed lanes are at stride 12 bytes
/// (3-float interleave), a unit the motif probe (cap 8) cannot capture, so the encoder
/// selects FloatRun (float32-lane sparse run) which is strictly smaller than byte-RLE.
/// </summary>
public class Test046_FloatRun_Stride12_f32 : ITestCase
{
    public int Id => 46;
    public string Name => "FloatRun_Stride12_f32";
    public int ExpectedDeltaSize => 1131; // ZeroRun(8 leading) + FloatRun strided float32 run
    public string[] Tags => new[] { "ml", "tensor", "float", "floatrun" };
    public string? Description => """
A 256×3 float32 buffer (3072 bytes) where only the 3rd channel (stride 12) is
incremented by +0.5f. Stride 12 exceeds the motif unit cap (8), so the encoder
emits a FloatRun (0x06) float32-lane sparse run, gated to a strict size win.
""";

    private const int N = 256, C = 3;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[N * C * 4];
        var span = MemoryMarshal.Cast<byte, float>(buf.AsSpan());
        var rng = new Random(0xDE17A20);
        for (int i = 0; i < span.Length; i++)
            span[i] = rng.NextSingle() - 0.5f;
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseFloats = MemoryMarshal.Cast<byte, float>(GenerateBase().Span).ToArray();
        for (int i = 0; i < N; i++)
            baseFloats[i * C + 2] += 0.5f; // 3rd channel only
        _next = MemoryMarshal.Cast<float, byte>(baseFloats).ToArray();
        return _next;
    }
}
