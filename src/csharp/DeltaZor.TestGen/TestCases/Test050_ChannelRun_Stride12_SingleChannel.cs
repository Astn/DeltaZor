using DZ.TestGen;

/// <summary>
/// Exercises the ChannelRun (0x08) opcode: a 32-unit, 12-byte-per-unit channel-interleaved
/// record (384 bytes, below the 4096 full-XOR threshold so it takes the motif/full-XOR encode
/// path) where ONLY byte offset 0 of each 12-byte unit changes, each by a distinct nonzero
/// value. The stride (12) EXCEEDS the motif unit cap (8) so motif can never lock (it falls back
/// to byte-RLE); the changed bytes are isolated so byte-RLE pays op+len+1 per change; FloatRun
/// (4-byte lanes) packs the 3 zero bytes inside each changed lane; HalfRun (2-byte lanes) packs
/// 1 zero byte per changed half-lane plus a per-half-lane bitmap over the whole span. ChannelRun
/// — which packs exactly the 1 changed byte/unit with a single channel mask and no per-lane
/// bitmap — is strictly smaller than ALL four alternatives, so the all-opcode-aware gate selects
/// it. ChannelRunCount must be 1.
/// </summary>
public class Test050_ChannelRun_Stride12_SingleChannel : ITestCase
{
    public int Id => 50;
    public string Name => "ChannelRun_Stride12_SingleChannel";
    public int ExpectedDeltaSize => 43; // 5 header + ChannelRun(stride12, mask{0}, 32 units, 32 changed bytes)
    public string[] Tags => new[] { "channel", "interleaved", "stride12", "channelrun" };
    public string? Description => """
A 32-unit × 12-byte channel-interleaved record (384 bytes) where only byte offset 0 of each
12-byte unit changes, each by a distinct nonzero value. Stride 12 exceeds the motif unit cap (8)
so motif cannot lock; isolated single-byte changes make byte-RLE expensive; FloatRun/HalfRun
waste the zero bytes inside their fixed 4-/2-byte lanes. ChannelRun (0x08) packs exactly the
changed byte per unit and wins — the gate selects it. ChannelRunCount == 1.
""";

    private const int Units = 32;
    private const int Stride = 12;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        return _base = new byte[Units * Stride]; // all-zero base ⇒ XOR == next
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[Units * Stride];
        for (int u = 0; u < Units; u++)
        {
            // Distinct nonzero byte per unit at channel offset 0 (defeats a Uniform packing);
            // (1 + (x & 0x7F)) is always in [1,128], never 0.
            buf[u * Stride] = (byte)(1 + ((u * 37) & 0x7F));
        }
        return _next = buf;
    }
}
