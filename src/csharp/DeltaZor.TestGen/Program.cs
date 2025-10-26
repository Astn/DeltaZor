using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using DZ;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DZ.TestGen;
using DZ.TestGen.TestCases;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

var tests = new ITestCase[]
{
    new Test001_Random1KB(),
    new Test002_Identical1KB(),
    new Test003_Sparse1KB(),
    new Test004_Mixed1KB(),
    new Test005_Extension1to2KB(),
    new Test006_RLEIdeal_ZeroRuns(),
    new Test007_RLEIdeal_NonZeroRuns(),
    new Test008_UDP_PacketSizes(),
    new Test009_ColorFill1080p(),
    new Test010_UniformInt32_1M(),
    new Test011_PlayerState_100(),
    new Test012_TerrainTile_64x64(),
    new Test013_Tensor_100x100_f32(),
    new Test014_UDP64_Sparse(),
    new Test015_UDP64_Mixed(),
    new Test016_UDP64_ZeroRun(),
    new Test017_UDP64_NonZeroRun(),
    new Test018_UDP128_Sparse(),
    new Test019_UDP128_Mixed(),
    new Test020_UDP128_ZeroRun(),
    new Test021_UDP128_NonZeroRun(),
    new Test022_UDP256_Sparse(),
    new Test023_UDP256_Mixed(),
    new Test024_UDP256_ZeroRun(),
    new Test025_UDP256_NonZeroRun(),
    new Test026_UDP512_Sparse(),
    new Test027_UDP512_Mixed(),
    new Test028_UDP512_ZeroRun(),
    new Test029_UDP512_NonZeroRun()
};

var manifest = new List<object>();
var outDir = Path.Combine(AppContext.BaseDirectory, "testdata");
Directory.CreateDirectory(outDir);

foreach (var t in tests)
{
    var initial = t.GenerateBase();
    var next = t.GenerateNext();

    var prevPath = Path.Combine(outDir, $"test{t.Id:000}.base.bin");
    var nextPath = Path.Combine(outDir, $"test{t.Id:000}.next.bin");
    var deltaPath = Path.Combine(outDir, $"test{t.Id:000}.delta.bin");
    var mdPath = Path.Combine(outDir, $"test{t.Id:000}.md");
    var prevImgPath = Path.Combine(outDir, $"test{t.Id:000}.base.png");
    var nextImgPath = Path.Combine(outDir, $"test{t.Id:000}.next.png");

    File.WriteAllBytes(prevPath, initial.ToArray());
    File.WriteAllBytes(nextPath, next.ToArray());

    // Generate delta
    var delta = new byte[initial.Length * 2 + 20];
    DeltaZor.CreateDelta(initial.Span, next.Span, delta, out int written);
    File.WriteAllBytes(deltaPath, delta[..written]);

    // === Generate Markdown ===
    var md = new StringBuilder();
    md.AppendLine($"# Test {t.Id:000}: {t.Name}");
    md.AppendLine();
    md.AppendLine($"**Tags:** `{string.Join("`, `", t.Tags)}`");
    md.AppendLine();
    md.AppendLine($"**Sizes:** initial: `{initial.Length}`, increment: `{next.Length}` bytes");
    md.AppendLine();
    md.AppendLine($"**DeltaZor:**  `{written}` bytes");
    md.AppendLine();

    // Class comment
    var classSummary = GetClassSummary(t.GetType());
    if (!string.IsNullOrWhiteSpace(classSummary))
    {
        md.AppendLine("## Details");
        md.AppendLine(classSummary);
        md.AppendLine();
    }

    // Description field
    if (!string.IsNullOrWhiteSpace(t.Description))
    {
        md.AppendLine("## Description");
        md.AppendLine(t.Description);
        md.AppendLine();
    }
// === Visual Preview ===
    md.AppendLine("## Visual Preview");
    md.AppendLine("<div class=\"preview-table\">");
    md.AppendLine("<table><tr>");
    md.AppendLine("<td><strong>Before</strong></td>");
    md.AppendLine("<td><strong>After</strong></td>");
    md.AppendLine("</tr><tr>");

    if (initial.Length < 1024)
    {
        md.AppendLine("<td><pre><code class=\"language-hex\">");
        md.AppendLine(HexDump(initial.Span));
        md.AppendLine("</code></pre></td>");
        md.AppendLine("<td><pre><code class=\"language-hex\">");
        md.AppendLine(HexDumpDiff(next.Span, initial.Span));
        md.AppendLine("</code></pre></td>");
        md.AppendLine("<td><pre><code class=\"language-hex\">");
        md.AppendLine(HexDump(delta.AsSpan(0..written)));
        md.AppendLine("</code></pre></td>");
    }
    else
    {
        SaveAsGrayscalePng(initial.Span, prevImgPath);
        SaveAsGrayscalePng(next.Span, nextImgPath);

        md.AppendLine($"<td><img src=\"{Path.GetFileName(prevImgPath)}\" width=\"400\"></td>");
        md.AppendLine($"<td><img src=\"{Path.GetFileName(nextImgPath)}\" width=\"400\"></td>");
    }

    md.AppendLine("</tr></table>");
    md.AppendLine("</div>");

    md.AppendLine();
    // Checksums
    md.AppendLine("## Checksums");
    md.AppendLine("```text");
    md.AppendLine($"SHA256(prev) = {ComputeSha256(prevPath)}");
    md.AppendLine($"SHA256(next) = {ComputeSha256(nextPath)}");
    md.AppendLine($"SHA256(delta)= {ComputeSha256(deltaPath)}");
    md.AppendLine("```");

    File.WriteAllText(mdPath, md.ToString());
    
    var entry = new
    {
        id = t.Id.ToString("000"),
        name = t.Name,
        prev = Path.GetFileName(prevPath),
        next = Path.GetFileName(nextPath),
        delta = Path.GetFileName(deltaPath),
        size_prev = initial.Length,
        size_next = next.Length,
        change_density = 1.0 - (initial.Span.SequenceEqual(next.Span) ? 1.0 : 0.0),
        sha256_prev = ComputeSha256(prevPath),
        sha256_next = ComputeSha256(nextPath),
        sha256_delta = ComputeSha256(deltaPath),
        expected_delta_size = t.ExpectedDeltaSize,
        tags = t.Tags,
        description = t.Description
    };

    manifest.Add(entry);
}

// Write manifest
File.WriteAllText(
    Path.Combine(outDir, "manifest.json"),
    JsonSerializer.Serialize(new { version = "1.0", tests = manifest }, new JsonSerializerOptions { WriteIndented = true })
);

// Write checksums
var checksums = manifest
    .Cast<dynamic>()
    .SelectMany(m => new[]
    {
        $"SHA256({m.prev})= {m.sha256_prev}",
        $"SHA256({m.next})= {m.sha256_next}",
        $"SHA256({m.delta})= {m.sha256_delta}"
    });
File.WriteAllLines(Path.Combine(outDir, "checksums.txt"), checksums);

Console.WriteLine($"Generated {tests.Length} test cases in {outDir}");

static string ComputeSha256(string path)
{
    using var sha = SHA256.Create();
    using var stream = File.OpenRead(path);
    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
}



static string? GetClassSummary(Type type)
{
    var comment = type.GetCustomAttribute<CompilerGeneratedAttribute>()?.ToString();
    var xml = $"{type.Namespace}.{type.Name}.xml";
    var path = Path.Combine(AppContext.BaseDirectory, xml);
    if (!File.Exists(path)) return null;

    var doc = XDocument.Load(path);
    var member = doc.Descendants("member")
        .FirstOrDefault(m => m.Attribute("name")?.Value == $"T:{type.FullName}");
    return member?.Descendants("summary").FirstOrDefault()?.Value.Trim();
}
static string HexDump(ReadOnlySpan<byte> data)
{
    var sb = new StringBuilder();
    for (int i = 0; i < data.Length; i += 16)
    {
        sb.Append($"{i:x4}: ");
        for (int j = 0; j < 16; j++)
        {
            if (i + j < data.Length)
                sb.Append($"{data[i + j]:x2} ");
            else
                sb.Append("   ");
        }
        sb.AppendLine();
    }
    return sb.ToString().TrimEnd();
}
static string HexDumpDiff(ReadOnlySpan<byte> data, ReadOnlySpan<byte> previous)
{
    var sb = new StringBuilder();
    var maxPos = Math.Min(data.Length, previous.Length);
    for (int i = 0; i < data.Length; i += 16)
    {
        sb.Append($"{i:x4}: ");
        for (int j = 0; j < 16; j++)
        {
            if (i + j < data.Length)
                if (i+j >=maxPos || data[i + j] == previous[i + j])
                    sb.Append($"{data[i + j]:x2} ");
                else
                    sb.Append($"<span style=\"color:#ff6b6b;\">{data[i + j]:x2}</span> ");
            else
                sb.Append("   ");
        }
        sb.AppendLine();
    }
    return sb.ToString().TrimEnd();
}

static void SaveAsGrayscalePng(ReadOnlySpan<byte> data, string path)
{
    int size = data.Length;
    int width = (int)Math.Ceiling(Math.Sqrt(size));
    int height = (width * (width - 1) < size) ? width : width - 1;

    var image = new Image<L8>(width, height);

    int i = 0;
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        if (i < size)
            image[x, y] = new L8(data[i++]);
        else
            image[x, y] = new L8(0);
    }

    image.SaveAsPng(path);
}