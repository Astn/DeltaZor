using DZ.TestGen;
using MemoryPack;


[MemoryPackable]
public unsafe partial struct TerrainTile(float height, byte biome, byte moisture)
{
    public float Height = height;
    public byte Biome = biome;
    public byte Moisture = moisture;
};
public class Test012_TerrainTile_64x64 : ITestCase
{  
    public int Id => 12;
    public string Name => "TerrainTile_64x64";
    public int ExpectedDeltaSize => 4000;
    public string[] Tags => new[] { "terrain", "grid", "sparse" };
    public string? Description => "64×64 terrain tiles, 10% modified";

    private const int Size = 64;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var tiles = new TerrainTile[Size * Size];
        var rng = new Random(0xDE17A20);
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new TerrainTile(
                height: rng.NextSingle() * 100,
                biome: (byte)rng.Next(0, 8),
                moisture: (byte)rng.Next(0, 100));

        }
        return _base = MemoryPackSerializer.Serialize(tiles);
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var tiles = MemoryPackSerializer.Deserialize<TerrainTile[]>(GenerateBase().Span);
        var rng = new Random(0xDE17A21);
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
            if ((x + y) % 10 == 0)
                tiles[y * Size + x].Height += rng.NextSingle() * 5;
        return _next = MemoryPackSerializer.Serialize(tiles);
    }
}