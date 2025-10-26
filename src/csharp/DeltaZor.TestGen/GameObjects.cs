namespace DZ.TestGen;

using MemoryPack;
using System.Numerics;

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

[MemoryPackable]
public partial record struct EntityComponent(
    ulong EntityId,
    Vector3 Position,
    Vector3 Scale,
    Quaternion Rotation,
    uint Flags,
    short TypeId
    );

[MemoryPackable]
public partial record struct TerrainTile(
    float Height,
    byte Biome,
    byte Moisture
    );

[MemoryPackable]
public partial record struct ColorBuffer1080p(
    // 1920×1080×3 = 9,331,200 bytes
    byte[] Pixels//[1920 * 1080 * 3]
);