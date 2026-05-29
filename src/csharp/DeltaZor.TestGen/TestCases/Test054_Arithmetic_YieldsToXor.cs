using System;
using DZ.TestGen;

/// <summary>
/// YIELD guard for the arithmetic modes (0x09/0x0A): 2048 bytes of unstructured random changes. No
/// uniform per-lane (any of widths 4/2/1/8) or per-plane (4/3/2) additive step exists, so neither
/// GlobalArithmetic nor PlanarArithmetic may fire — the region must fall through to the unchanged
/// XOR/motif pipeline. Proves the arithmetic probes do not steal non-arithmetic data. Both
/// ArithmeticCount and PlanarCount must be 0; the delta is the ordinary XOR/RLE encoding and must
/// round-trip exactly (and be byte-identical C#↔Zig).
/// </summary>
public class Test054_Arithmetic_YieldsToXor : ITestCase
{
    public int Id => 54;
    public string Name => "Arithmetic_YieldsToXor";
    public int ExpectedDeltaSize => 2086; // ordinary XOR/RLE of fully random change (no arithmetic)
    public string[] Tags => new[] { "arithmetic", "yield", "random" };
    public string? Description => """
2048 bytes of fully random changes with no uniform additive structure. Neither GlobalArithmetic
(0x09) nor PlanarArithmetic (0x0A) fires; the region falls through to the XOR/RLE pipeline.
ArithmeticCount == 0 and PlanarCount == 0.
""";

    private const int N = 2048;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[N];
        new Random(0x4E1D0364).NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var buf = new byte[N];
        new Random(0x4E1D9999).NextBytes(buf);
        return _next = buf;
    }
}
