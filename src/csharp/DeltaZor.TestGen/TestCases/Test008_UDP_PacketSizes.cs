using System.Numerics;
using System.Runtime.InteropServices;
using DZ.TestGen;
using MemoryPack;

public class Test008_UDP_PacketSizes : ITestCase
{
    public int Id => 8;
    public string Name => "UDP_PacketSizes_64_128_256_512";
    public int ExpectedDeltaSize => 380; // Sum of all 4 deltas (~28 + 40 + 80 + 240)
    public string[] Tags => new[] { "udp", "inet", "packet", "realtime", "sparse", "rle", "arithmetic" };
    public string? Description => "64B, 128B, 256B, 512B buffers: sparse + uniform + length change";

    private readonly (int size, string suffix)[] _sizes = new[]
    {
        (64,  "064"),
        (128, "128"),
        (256, "256"),
        (512, "512")
    };

    private readonly Dictionary<string, ReadOnlyMemory<byte>> _baseCache = new();
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _nextCache = new();

    public ReadOnlyMemory<byte> GenerateBase()
    {
        // Return dummy — base case not used
        return ReadOnlyMemory<byte>.Empty;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        // Return dummy — next case not used
        return ReadOnlyMemory<byte>.Empty;
    }

    // --- Custom file-based access ---
    public string GetPrevPath(int size) => $"test{Id:000}_{size:D3}.prev.bin";
    public string GetNextPath(int size) => $"test{Id:000}_{size:D3}.next.bin";

    public ReadOnlyMemory<byte> GetBase(int size)
    {
        string key = $"base_{size}";
        if (_baseCache.TryGetValue(key, out var mem)) return mem;

        var buf = new byte[size];
        var rng = new Random(0xDE17A20 + size);
        rng.NextBytes(buf);

        // Pattern: first 1/4 = player state, rest = padding
        if (size >= 32)
        {
            var player = new PlayerState(
                Position: new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()),
                Rotation: Quaternion.Identity,
                Velocity: Vector3.Zero,
                Health: 100f,
                Mana: 50f,
                Level: 1,
                Id: 1
            );
            MemoryPackSerializer.Serialize(player).CopyTo(buf.AsSpan());
        }

        return _baseCache[key] = buf;
    }

    public ReadOnlyMemory<byte> GetNext(int size)
    {
        string key = $"next_{size}";
        if (_nextCache.TryGetValue(key, out var mem)) return mem;

        var baseSpan = GetBase(size).Span;
        var buf = new byte[size];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();
        var rng = new Random(0xDE17A21 + size);

        // === Real-time delta patterns ===
        switch (size)
        {
            case 64:
                // Sparse: modify 8 bytes (e.g. position)
                for (int i = 0; i < 8; i++) span[i] = (byte)rng.Next(256);
                break;

            case 128:
                // Uniform XOR run: 64-byte run of 0xAA
                for (int i = 0; i < 64; i++) span[i] ^= 0xAA;
                break;

            case 256:
                // Mixed: 50% XOR flip + one int32 += 5
                for (int i = 0; i < 128; i += 2) span[i] ^= 0xFF;
                if (size >= 4)
                    BitConverter.GetBytes(BitConverter.ToInt32(span.Slice(128, 4)) + 5).CopyTo(span.Slice(128, 4));
                break;

            case 512:
                // Length change + sparse: grow to 512, modify first 100 bytes
                var grown = new byte[512];
                baseSpan.CopyTo(grown);
                for (int i = 0; i < 100; i++) grown[i] = (byte)(i % 256);
                rng.NextBytes(grown.AsSpan(256));
                return _nextCache[key] = grown;
        }

        return _nextCache[key] = buf;
    }
}