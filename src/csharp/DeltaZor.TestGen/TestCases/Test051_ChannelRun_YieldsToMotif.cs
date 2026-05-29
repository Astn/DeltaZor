using DZ.TestGen;

/// <summary>
/// TASK-0363 gate guard: ChannelRun (0x08) must YIELD on channel-interleaved data whose stride
/// is WITHIN the motif unit cap (8), where motif already encodes the shape more cheaply. A
/// 32-pixel RGBA array (stride 4, 128 bytes, below the 4096 full-XOR threshold) where only the
/// R channel (byte offset 0) of each pixel changes, each by a distinct nonzero value. ChannelRun
/// deliberately probes ONLY strides > 8 (the gap motif cannot reach), so it never even probes
/// stride 4; the varying-motif opcode (unit 4, mask {0}) locks and wins. Even if ChannelRun had
/// probed a stride-12 view of this data, its all-opcode-aware gate would reject because the
/// motif/RLE cost over the span is smaller. Proves ChannelRun does NOT steal the motif path on
/// realistic channel-interleaved input: ChannelRunCount must be 0, VaryingMotifCount > 0.
/// </summary>
public class Test051_ChannelRun_YieldsToMotif : ITestCase
{
    public int Id => 51;
    public string Name => "ChannelRun_YieldsToMotif";
    public int ExpectedDeltaSize => 117; // lead + varying motif (unit 4, mask {R}) — ChannelRun yields
    public string[] Tags => new[] { "channel", "interleaved", "rgba", "stride4", "channelrun", "motif", "gate", "regression" };
    public string? Description => """
A 32-pixel RGBA array (stride 4, 128 bytes) where only the R channel (offset 0) of each pixel
changes, each by a distinct nonzero value. Stride 4 is within the motif unit cap (8), so the
varying-motif opcode (unit 4, mask {0}) encodes it cheaply. ChannelRun probes only strides > 8,
so it yields entirely to motif here. ChannelRunCount == 0, VaryingMotifCount > 0.
""";

    private const int Pixels = 32;
    private const int Stride = 4; // RGBA
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        return _base = new byte[Pixels * Stride]; // all-zero base ⇒ XOR == next
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[Pixels * Stride];
        for (int p = 0; p < Pixels; p++)
        {
            // Only the R channel changes, distinct nonzero per pixel.
            buf[p * Stride] = (byte)(1 + ((p * 37) & 0x7F));
        }
        return _next = buf;
    }
}
