using System.Numerics;
using System.Runtime.InteropServices;
using DZ.TestGen;
using MemoryPack;

public class Test010_UniformInt32_1M : ITestCase
{
    public int Id => 10;
    public string Name => "Uniform_Int32_1M";
    public int ExpectedDeltaSize => 14; // [0x09][4][05 00 00 00][laneCount=1048576] + 5 header (TASK-0364)
    public string[] Tags => new[] { "uniform", "arithmetic", "int32", "1m" };
    public string? Description => "1,048,576 × int32, all += 5";

    private const int Count = 1_048_576;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Count * 4];
        var span = MemoryMarshal.Cast<byte, int>(buf.AsSpan());
        var rng = new Random(0xDE17A20);
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


[MemoryPackable]
public partial record struct PlayerState(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Velocity,
    float Health,
    float Mana,
    int Level,
    ulong Id
);