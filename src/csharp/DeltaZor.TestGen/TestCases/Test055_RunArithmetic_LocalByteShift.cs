using System;
using DZ.TestGen;

/// <summary>
/// Exercises the RunArithmetic (0x0B) opcode (TASK-0365): a 2048-byte buffer that is a single
/// arithmetic SEGMENT — bytes [600,1400) all shifted by a constant +7 (byte wraparound) — surrounded
/// by unchanged regions. Whole-region GlobalArithmetic (0x09) / PlanarArithmetic (0x0A) cannot fire
/// because uniformity breaks at the segment edges; per-run RunArithmetic captures the 800-byte
/// segment in a 4-byte opcode ([0x0B][flags=0][step=7][runLen]) between two ZeroRuns. RunArithmetic
/// Count == 1; ArithmeticCount == 0; PlanarCount == 0.
/// </summary>
public class Test055_RunArithmetic_LocalByteShift : ITestCase
{
    public int Id => 55;
    public string Name => "RunArithmetic_LocalByteShift";
    public int ExpectedDeltaSize => 0; // size-agnostic; cross-toolchain byte-parity is the contract
    public string[] Tags => new[] { "arithmetic", "run", "per-run", "local", "wraparound" };
    public string? Description => """
A 2048-byte buffer with a single local +7 arithmetic segment over [600,1400), byte wraparound,
surrounded by unchanged regions. Whole-region 0x09/0x0A can't fire (uniformity breaks at the edges);
per-run RunArithmetic (0x0B) captures the segment in one 4-byte opcode. RunArithmeticCount == 1.
""";

    private const int Size = 2048;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size];
        var rng = new Random(0x5A1A0365);
        rng.NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[Size];
        baseSpan.CopyTo(buf);
        for (int i = 600; i < 1400; i++)
            buf[i] = (byte)(baseSpan[i] + 7); // local +7 wraparound segment
        return _next = buf;
    }
}
