using System;
using DZ.TestGen;

/// <summary>
/// Exercises the clamp-aware RunArithmetic (0x0B flags bit0=1) variant (TASK-0365): two local runs
/// whose intended signed step SATURATES at a byte boundary. Ceiling run [200,360): old ramps
/// 220,221,…255,255,… and new = clamp(old+10) (so 250→255, 255→255). Floor run [600,760): old ramps
/// 25,24,…0,0,… and new = clamp(old-10) (so 5→0, 0→0). Each run contains non-saturated bytes that
/// anchor the exact step, and saturated bytes the decoder reproduces by replaying clamp(old+step) on
/// the still-untouched old byte — LOSSLESS (no exception list). Pure wraparound CANNOT encode these
/// (the boundary bytes break a single mod-256 step), so clamp mode is required. RunArithmeticCount
/// >= 1; round-trip exact at both saturation boundaries.
/// </summary>
public class Test056_RunArithmetic_ClampBoundary : ITestCase
{
    public int Id => 56;
    public string Name => "RunArithmetic_ClampBoundary";
    public int ExpectedDeltaSize => 0; // size-agnostic; cross-toolchain byte-parity is the contract
    public string[] Tags => new[] { "arithmetic", "run", "clamp", "saturation", "boundary" };
    public string? Description => """
Two local saturating runs: a ceiling run new=clamp(old+10) over [200,360) (old reaches 255) and a
floor run new=clamp(old-10) over [600,760) (old reaches 0). Pure wraparound can't encode these; the
clamp-aware RunArithmetic (0x0B flags bit0=1) records the signed step and the decoder replays
clamp(old+step) exactly on the untouched old byte — lossless at both boundaries.
""";

    private const int Size = 1024;
    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[Size]; // background identical (zeros)
        // Ceiling run: old ramps 220.. up to 255 then holds 255.
        for (int i = 200; i < 360; i++)
            buf[i] = (byte)Math.Min(255, 220 + (i - 200));
        // Floor run: old ramps 25.. down to 0 then holds 0.
        for (int i = 600; i < 760; i++)
            buf[i] = (byte)Math.Max(0, 25 - (i - 600));
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[Size];
        baseSpan.CopyTo(buf);
        for (int i = 200; i < 360; i++)
        {
            int r = baseSpan[i] + 10; if (r > 255) r = 255;
            buf[i] = (byte)r; // clamp(old + 10)
        }
        for (int i = 600; i < 760; i++)
        {
            int r = baseSpan[i] - 10; if (r < 0) r = 0;
            buf[i] = (byte)r; // clamp(old - 10)
        }
        return _next = buf;
    }
}
