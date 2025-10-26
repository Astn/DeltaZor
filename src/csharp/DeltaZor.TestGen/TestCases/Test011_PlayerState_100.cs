using System.Numerics;
using DZ.TestGen;
using MemoryPack;

public class Test011_PlayerState_100 : ITestCase
{
    public int Id => 11;
    public string Name => "PlayerState_100";
    public int ExpectedDeltaSize => 800;
    public string[] Tags => new[] { "game", "entity", "memorypack", "sparse" };
    public string? Description => "100 PlayerState structs, 20% fields changed";

    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var players = new PlayerState[100];
        var rng = new Random(0xDE17A20);
        for (int i = 0; i < 100; i++)
        {
            players[i] = new(
                Position: new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()),
                Rotation: Quaternion.CreateFromYawPitchRoll(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()),
                Velocity: new Vector3(rng.NextSingle() * 10, rng.NextSingle() * 10, rng.NextSingle() * 10),
                Health: 100f,
                Mana: 50f,
                Level: i % 10,
                Id: (ulong)i
            );
        }
        return _base = MemoryPackSerializer.Serialize(players);
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseObj = MemoryPackSerializer.Deserialize<PlayerState[]>(GenerateBase().Span);
        var rng = new Random(0xDE17A21);
        for (int i = 0; i < baseObj.Length; i += 5)
        {
            baseObj[i].Health = Math.Max(0, baseObj[i].Health - 10);
            baseObj[i].Mana = Math.Min(100, baseObj[i].Mana + 15);
            baseObj[i].Position = baseObj[i].Position with { X = baseObj[i].Position.X + rng.NextSingle() * 2 };
        }
        return _next = MemoryPackSerializer.Serialize(baseObj);
    }
}