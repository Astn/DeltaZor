using System.Numerics;

namespace DZ.TestGen;

public static class Deterministic
{
    private static readonly Random Rng = new(0xDE17A20);

    public static byte Byte() => (byte)Rng.Next(0, 256);
    public static ushort UShort() => (ushort)Rng.Next(0, 65536);
    public static float Float() => (float)Rng.NextDouble();
    public static Vector3 Vec3() => new(Float(), Float(), Float());
    public static Quaternion Quat() => Quaternion.CreateFromYawPitchRoll(Float(), Float(), Float());
}