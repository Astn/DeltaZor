using System;
using System.Runtime.InteropServices;
using DZ.TestGen;

/// <summary>
/// Exercises the GlobalArithmetic (0x09) opcode: 1024 int32 values whose new value is the old
/// value + 5 (a uniform global arithmetic shift). The XOR-delta of x vs x+5 is carry-dependent
/// noise that the XOR-stream opcodes (RLE/motif/float/half/channel) encode poorly (≈1.5 KB), while
/// the arithmetic difference is a constant +5 per int32 lane — captured by a single 0x09 opcode
/// covering the whole 4096-byte region in ≈9 bytes. ArithmeticCount must be 1; PlanarCount 0.
/// </summary>
public class Test052_GlobalArithmetic_Int32Ramp : ITestCase
{
    public int Id => 52;
    public string Name => "GlobalArithmetic_Int32Ramp";
    public int ExpectedDeltaSize => 13; // 5 header + [0x09][4][05 00 00 00][laneCount=1024]
    public string[] Tags => new[] { "arithmetic", "global", "int32", "ramp" };
    public string? Description => """
1024 int32 values, all += 5. XOR-delta is carry-dependent noise the XOR opcodes can't compress;
the constant +5 per-lane arithmetic difference is captured by one GlobalArithmetic (0x09) opcode
over the whole region. ArithmeticCount == 1.
""";

    private const int Count = 1024;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Count * 4];
        var span = MemoryMarshal.Cast<byte, int>(buf.AsSpan());
        var rng = new Random(0x5A1A0364);
        for (int i = 0; i < Count; i++)
            span[i] = rng.Next();
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = MemoryMarshal.Cast<byte, int>(GenerateBase().Span);
        var buf = new byte[baseSpan.Length * 4];
        var nextSpan = MemoryMarshal.Cast<byte, int>(buf.AsSpan());
        for (int i = 0; i < Count; i++)
            nextSpan[i] = baseSpan[i] + 5;
        return _next = buf;
    }
}
