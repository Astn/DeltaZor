using DZ.TestGen;

/// <summary>
/// Exercises the HalfRun (0x07) opcode: a 256-lane float16 array (512 bytes, below the 4096
/// full-XOR threshold so it takes the motif/full-XOR encode path) where a sparse, IRREGULAR
/// set of half-lanes changes — lane indices i ≡ 9 (mod 11). The period is 22 bytes, well
/// beyond the motif unit cap (8), so motif cannot lock; the changed half-lanes are isolated
/// (zero neighbours) so byte-RLE pays op+len+2 per change; and each changed 4-byte word holds
/// only ONE changed half so FloatRun (0x06) would waste 2 bytes per lane. HalfRun (2-byte
/// lanes + per-lane bitmap) is strictly smaller than ALL three alternatives, so the
/// FloatRun-aware + motif-aware gate selects HalfRun. HalfPatternCount must be 1.
/// </summary>
public class Test048_HalfRun_SparseStride11_f16 : ITestCase
{
    public int Id => 48;
    public string Name => "HalfRun_SparseStride11_f16";
    public int ExpectedDeltaSize => 90; // 5 header + ZeroRun(9 lanes) + HalfRun(243 lanes,23 changed) + trailing ZeroRun
    public string[] Tags => new[] { "ml", "tensor", "half", "float16", "halfrun" };
    public string? Description => """
A 256-lane float16 array (512 bytes) where half-lanes at i ≡ 9 (mod 11) each receive a
distinct nonzero 2-byte XOR delta, isolated by zero lanes. Period 22 bytes exceeds the motif
unit cap (8); changes occupy only one half of each 4-byte word (FloatRun would waste 2 B/lane);
isolated 2-byte changes make byte-RLE expensive. HalfRun (0x07) wins and the gate selects it.
""";

    private const int N = 256; // float16 lanes
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        return _base = new byte[N * 2]; // all-zero base ⇒ XOR == next
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[N * 2];
        for (int i = 0; i < N; i++)
        {
            if (i % 11 != 9) continue;               // irregular sparse set, period 22 bytes
            // Distinct nonzero 2-byte half value per lane (defeats a Uniform motif).
            ushort h = (ushort)(0x8000 | (i * 37 + 1));
            buf[i * 2] = (byte)(h & 0xFF);
            buf[i * 2 + 1] = (byte)(h >> 8);
        }
        return _next = buf;
    }
}
