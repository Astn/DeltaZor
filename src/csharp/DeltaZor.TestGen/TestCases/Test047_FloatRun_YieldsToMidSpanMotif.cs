using DZ.TestGen;

/// <summary>
/// TASK-0361 codex REJECT-B regression guard. A 416-byte XOR shape where a changed FIRST
/// float32 lane (byte 0 = 0x01) is followed by three zero lanes and then a Uniform motif
/// (100 repeats of the 4-byte unit 0xAA 00 00 00, unit 4, mask 1) starting at byte 16.
///
/// The maximal-span FloatRun probe (pre-fix) swallowed the whole region as one FloatRun
/// (425-byte delta) because motif TryStart failed only at byte 0. The motif-aware gate now
/// makes FloatRun YIELD: it caps before the first motif-able byte position, so the encoder
/// emits NonZeroRun(1) + ZeroRun(15) + Uniform-motif(100) for a 16-byte delta — a strict
/// improvement. FloatPatternCount must be 0 here; the motif path wins.
///
/// Base is all-zero so the XOR stream == next (the codex adversarial vector verbatim).
/// </summary>
public class Test047_FloatRun_YieldsToMidSpanMotif : ITestCase
{
    public int Id => 47;
    public string Name => "FloatRun_YieldsToMidSpanMotif";
    public int ExpectedDeltaSize => 16; // NonZeroRun(1) + ZeroRun(15) + Uniform motif(100) — FloatRun yields
    public string[] Tags => new[] { "ml", "float", "floatrun", "motif", "regression", "gate" };
    public string? Description => """
416-byte XOR: a changed first float32 lane (byte0=0x01), three zero lanes, then a Uniform
motif (100×[0xAA,0,0,0], unit 4) from byte 16. Guards the TASK-0361 codex REJECT-B fix:
FloatRun must YIELD to the mid-span motif (cap before the first motif-able position) instead
of swallowing it. Optimal encode = NonZeroRun(1)+ZeroRun(15)+UniformMotif(100) = 16 bytes;
the pre-fix maximal-span FloatRun regressed to 425 bytes.
""";

    private const int Len = 416;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        return _base = new byte[Len]; // all-zero base ⇒ XOR == next
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[Len];
        buf[0] = 0x01;                       // first lane changed; motif TryStart fails at byte 0
        for (int u = 0; u < 100; u++)        // Uniform motif starting at byte 16
            buf[16 + u * 4] = 0xAA;
        return _next = buf;
    }
}
