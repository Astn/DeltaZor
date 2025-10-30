using DZ.TestGen;

public class GenericVerticalStripeTest : ITestCase
{
    public int Id { get; }
    public string Name { get; }
    public int ExpectedDeltaSize { get; }
    public string[] Tags { get; }
    public string? Description { get; }

    private readonly int _size;
    private readonly uint _seed;
    private readonly int _changeSize;
    private readonly int _unchangedSize;
    private readonly int _extraChangeSize;
    private readonly int _extraUnchangedSize;
    private readonly int _offset;
    private readonly int _intervalMultiplier;
    private readonly Func<byte, byte> _modifier;

    private ReadOnlyMemory<byte> _base = ReadOnlyMemory<byte>.Empty;
    private ReadOnlyMemory<byte> _next = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Initializes a parameterized vertical stripe test case.
    /// </summary>
    /// <param name="id">Unique test ID.</param>
    /// <param name="namePrefix">Prefix for generated name (e.g., "D").</param>
    /// <param name="size">Buffer size (e.g., 512).</param>
    /// <param name="seed">Random seed for base data (uint for 32-bit reproducibility).</param>
    /// <param name="changeSize">Bytes to change per interval.</param>
    /// <param name="unchangedSize">Bytes to leave unchanged per interval.</param>
    /// <param name="extraChangeSize">Additional burst size in change runs (for higher-level patterns).</param>
    /// <param name="extraUnchangedSize">Additional gap size in unchanged runs (for higher-level patterns).</param>
    /// <param name="offset">Starting offset for pattern phase shift.</param>
    /// <param name="intervalMultiplier">Scales interval for broader patterns (default 1).</param>
    /// <param name="modifier">Operation to apply (e.g., b => (byte)(b ^ mask)).</param>
    /// <param name="expectedDeltaSize">Expected compressed delta size.</param>
    /// <param name="tags">Test tags (e.g., "udp", "mixed").</param>
    /// <param name="descriptionTemplate">Template for description (e.g., "{size}B packet: {change}/{total}ths {op}-flipped vertical").</param>
    public GenericVerticalStripeTest(
        int id,
        string namePrefix,
        int size,
        uint seed,
        int changeSize,
        int unchangedSize,
        int extraChangeSize = 0,
        int extraUnchangedSize = 0,
        int offset = 0,
        int intervalMultiplier = 1,
        Func<byte, byte>? modifier = null,
        int expectedDeltaSize = 0,
        string[]? tags = null,
        string? descriptionTemplate = null)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (changeSize < 0 || unchangedSize < 0) throw new ArgumentOutOfRangeException("Sizes must be non-negative.");
        if (intervalMultiplier < 1) throw new ArgumentOutOfRangeException(nameof(intervalMultiplier));

        Id = id;
        _size = size;
        _seed = seed;
        _changeSize = changeSize;
        _unchangedSize = unchangedSize;
        _extraChangeSize = extraChangeSize;
        _extraUnchangedSize = extraUnchangedSize;
        _offset = offset;
        _intervalMultiplier = intervalMultiplier;
        _modifier = modifier ?? (b => (byte)(b ^ 0xFF)); // Default: XOR flip

        int interval = (_changeSize + _unchangedSize) * _intervalMultiplier;
        Name = $"{namePrefix}{size}_Vertical_{changeSize}_by_{unchangedSize}" +
               (extraChangeSize > 0 || extraUnchangedSize > 0 ? "_Burst" : "") +
               (offset != 0 ? $"_Offset{offset}" : "") +
               (intervalMultiplier > 1 ? $"_x{intervalMultiplier}" : "");
        ExpectedDeltaSize = expectedDeltaSize;
        Tags = tags ?? new[] { "udp", $"{size}b", "mixed", "vertical" };
        Description = descriptionTemplate?.Replace("{size}", size.ToString())
                          .Replace("{change}", changeSize.ToString())
                          .Replace("{total}", interval.ToString())
                          .Replace("{op}", modifier == null ? "XOR" : "modified")
                      ?? $"{size}B packet: {changeSize}/{interval}ths modified vertical" +
                      (extraChangeSize > 0 ? $" with extra change {extraChangeSize}" : "") +
                      (extraUnchangedSize > 0 ? $" with extra unchanged {extraUnchangedSize}" : "");
    }

    public ReadOnlyMemory<byte> GenerateBase()
    {
        if (!_base.IsEmpty) return _base;
        var buf = new byte[_size];
        // Use fixed seed for reproducibility; Random is allocation-free post-init.
        var rand = new Random((int)_seed);
        rand.NextBytes(buf);
        return _base = buf;
    }

    public ReadOnlyMemory<byte> GenerateNext()
    {
        if (!_next.IsEmpty) return _next;
        var baseSpan = GenerateBase().Span;
        var buf = new byte[_size];
        baseSpan.CopyTo(buf);
        var span = buf.AsSpan();

        int interval = (_changeSize + _unchangedSize) * _intervalMultiplier;
        for (int i = _offset; i < _size; i += interval)
        {
            // Core change run with extra burst
            int effectiveChange = _changeSize + _extraChangeSize;
            for (int j = 0; j < effectiveChange; j++)
            {
                if (i + j < _size)
                {
                    span[i + j] = _modifier(span[i + j]);
                }
            }

            // Skip unchanged with extra gap (implicit in loop, but adjust for higher-level)
            int effectiveUnchanged = _unchangedSize + _extraUnchangedSize;
            // No explicit action needed; loop increment handles it.
        }
        return _next = buf;
    }
}