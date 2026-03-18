using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using DZ.TestGen;

/// <summary>
/// Represents a test case that generates and manipulates a 100×100×3 float32 tensor
/// with values modified using arithmetic operations. The tensor is initialized with
/// uniform random values centered around zero (-.5 to .5) and incremented by a set delta (+0.1f) for next version.
/// </summary>
public class Test013_Tensor_100x100_f32 : ITestCase
{
    public int Id => 13;
    public string Name => "Tensor_100x100_f32";
    public int ExpectedDeltaSize => 24;
    public string[] Tags => new[] { "ml", "tensor", "arithmetic", "float" };
    public string? Description => """
Represents a test case that generates and manipulates a 100×100×3 float32 tensor
with values modified using arithmetic operations. The tensor is initialized with
uniform random values centered around zero (-.5 to .5) and incremented by a set delta (+0.5f) for the third dimension.
""";

    private const int W = 100, H = 100, C = 3;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[W * H * C * 4];
        var span = MemoryMarshal.Cast<byte, float>(buf.AsSpan());
        var rng = new Random(0xDE17A20);
        for (int i = 0; i < span.Length; i++)
            span[i] = rng.NextSingle() - 0.5f;
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        // Return cached result if already generated
        if (!_next.IsEmpty) return _next;

        // 1. Get the base data as ReadOnlyMemory<float>
        ReadOnlyMemory<byte> baseData = GenerateBase(); // assumes returns ReadOnlyMemory<float>

        // 2. We need a *mutable* copy to modify
        var mutableCopy = MemoryMarshal.Cast<byte,float>(baseData.Span).ToArray(); // only copy we allow

        // 3. Create tensor view over the mutable array
        var tensor = Tensor.Create<float>(
            mutableCopy,
            0,
            new[] { (IntPtr)W, (IntPtr)H, (IntPtr)C },
            strides: new[] { (IntPtr)(H * C), (IntPtr)C, (IntPtr)1 }
        );

        // 4. Get mutable TensorSpan for safe indexing
        TensorSpan<float> ts = tensor.AsTensorSpan();

        // 5. Modify the 3rd channel (index 2) — add to every pixel in channel 2
        for (int i = 0; i < W; i++)
        for (int j = 0; j < H; j++)
        {
            ts[i, j, 2] += 0.5f;
        }

        // 6. Convert modified float[] back to ReadOnlyMemory<byte>
        _next = MemoryMarshal.Cast<float, byte>(mutableCopy).ToArray();

        return _next;
    }
}