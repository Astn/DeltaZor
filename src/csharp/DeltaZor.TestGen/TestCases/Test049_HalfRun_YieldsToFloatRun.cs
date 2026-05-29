using DZ.TestGen;

/// <summary>
/// TASK-0362 gate guard: HalfRun (0x07) must YIELD to FloatRun (0x06) on 4-byte-dense shapes.
/// A 256-lane float32 array (1024 bytes, below the 4096 full-XOR threshold) where every 3rd
/// float32 lane (stride 12 bytes, beyond the motif unit cap 8) receives a FULL 4-byte nonzero
/// XOR delta — BOTH float16 halves of each changed word are nonzero. HalfRun's packed data
/// would equal FloatRun's (2 halves × 2 B == one 4-byte lane) but its bitmap has 2× the bits,
/// so halfSize > floatSize: the HalfRun gate's FloatRun term rejects, HalfRun declines, and
/// the FloatRun probe (second) fires. Proves the deterministic no-double-fire selection:
/// HalfPatternCount must be 0 and FloatPatternCount must be 1.
/// </summary>
public class Test049_HalfRun_YieldsToFloatRun : ITestCase
{
    public int Id => 49;
    public string Name => "HalfRun_YieldsToFloatRun";
    public int ExpectedDeltaSize => 389; // lead motif/ZeroRun + FloatRun strided float32 run (HalfRun yields)
    public string[] Tags => new[] { "ml", "tensor", "half", "float", "halfrun", "floatrun", "gate", "regression" };
    public string? Description => """
A 256-lane float32 array (1024 bytes) where every 3rd lane (stride 12 > motif cap 8) gets a
full 4-byte nonzero delta — both float16 halves nonzero. HalfRun would cost more than FloatRun
(2× bitmap bits for the same packed data), so its FloatRun-aware gate rejects and HalfRun
YIELDS: the FloatRun (0x06) probe fires instead. HalfPatternCount == 0, FloatPatternCount == 1.
""";

    private const int N = 256; // float32 lanes
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        return _base = new byte[N * 4]; // all-zero base ⇒ XOR == next
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[N * 4];
        for (int i = 0; i < N; i++)
        {
            if (i % 3 != 0) continue;                 // strided float32 lanes, stride 12 bytes
            // Distinct full 4-byte delta with EVERY byte guaranteed nonzero ⇒ both float16
            // halves of each word are nonzero, so HalfRun's packed data == FloatRun's but its
            // bitmap is 2× larger ⇒ halfSize > floatSize ⇒ HalfRun yields. (1 + (x & 0x7F) is
            // always in [1,128], never 0.)
            buf[i * 4]     = (byte)(1 + ((i * 7) & 0x7F));
            buf[i * 4 + 1] = (byte)(1 + ((i * 13) & 0x7F));
            buf[i * 4 + 2] = (byte)(1 + ((i * 29) & 0x7F));
            buf[i * 4 + 3] = (byte)(1 + ((i * 53) & 0x7F));
        }
        return _next = buf;
    }
}
