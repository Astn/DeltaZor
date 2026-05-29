using System;
using DZ.TestGen;

/// <summary>
/// Exercises the PlanarArithmetic (0x0A) opcode: 1000 RGBA pixels where each channel shifts by its
/// own constant (R+10, G-6 via byte wraparound, B+1, A+0) — a color tint. GlobalArithmetic (0x09)
/// cannot capture this (the per-int32-lane difference is not uniform across the 4 differing
/// channels), and the XOR-delta is carry-dependent noise. The per-plane arithmetic difference IS
/// uniform per channel, so a single 0x0A opcode (planeCount=4) covers the whole 4000-byte region in
/// ≈8 bytes. PlanarCount must be 1; ArithmeticCount 0.
/// </summary>
public class Test053_PlanarArithmetic_RgbaTint : ITestCase
{
    public int Id => 53;
    public string Name => "PlanarArithmetic_RgbaTint";
    public int ExpectedDeltaSize => 13; // 5 header + [0x0A][4][0A FA 01 00][unitCount=1000]
    public string[] Tags => new[] { "arithmetic", "planar", "rgba", "tint" };
    public string? Description => """
1000 RGBA pixels, each channel shifted by its own constant (R+10, G-6 wrap, B+1, A+0). Global
arithmetic can't capture differing per-channel steps; the per-plane uniform difference is captured
by one PlanarArithmetic (0x0A) opcode over the whole region. PlanarCount == 1.
""";

    private const int Px = 1000;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Px * 4];
        var rng = new Random(0xB1A0364);
        rng.NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var b = GenerateBase().Span;
        var buf = new byte[Px * 4];
        for (int i = 0; i < Px; i++)
        {
            buf[i * 4]     = (byte)(b[i * 4]     + 10);  // R + 10
            buf[i * 4 + 1] = (byte)(b[i * 4 + 1] + 250); // G - 6 (wraparound)
            buf[i * 4 + 2] = (byte)(b[i * 4 + 2] + 1);   // B + 1
            buf[i * 4 + 3] = b[i * 4 + 3];               // A + 0
        }
        return _next = buf;
    }
}
