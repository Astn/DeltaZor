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
using DZ.Shared;
namespace DZ.TestGen;
public class Program
{
    public static void Main(string[] args)
    {
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
            new Test029_UDP512_NonZeroRun(),
            new Test030_512_Vertical_1_by_3(),
            new Test031_512_Vertical_2_by_2(),
            new Test032_512_Vertical_2_by_6(),
            new Test033_512_Vertical_2_by_14(),
            new GenericVerticalStripeTest(
                id: 34,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A20u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 201, // Placeholder; simulated RLE estimate (actual may be lower with motifs)
                tags: new[] { "udp", "512b", "mixed", "xor", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped vertical"
            ),
            new GenericVerticalStripeTest(
                id: 35,
                namePrefix: "D",
                size: 4096,
                seed: 0xDE17A21u,
                changeSize: 4,
                unchangedSize: 4,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 2049, // Placeholder; larger buffer for SIMD stress, motif-aligned (unit=8)
                tags: new[] { "large", "4096b", "mixed", "xor", "motif" },
                descriptionTemplate: "{size}B buffer: {change}/{total}ths {op}-flipped, motif-aligned"
            ),
            new GenericVerticalStripeTest(
                id: 36,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A22u,
                changeSize: 1,
                unchangedSize: 3,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 385, // Placeholder; sparse channel-like for uniform motif (unit=4)
                tags: new[] { "udp", "512b", "sparse", "xor", "motif" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped, sparse motif test"
            ),
            new GenericVerticalStripeTest(
                id: 37,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A23u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 2,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 225, // Placeholder; bursty changes for varying motif detection
                tags: new[] { "udp", "512b", "bursty", "xor", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped with bursts"
            ),
            new GenericVerticalStripeTest(
                id: 38,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A24u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 0,
                extraUnchangedSize: 4,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 169, // Placeholder; extra gaps for longer zero runs, RLE efficiency
                tags: new[] { "udp", "512b", "gappy", "xor", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped with extra gaps"
            ),
            new GenericVerticalStripeTest(
                id: 39,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A25u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 8,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 201, // Placeholder; phase shift for boundary/misalignment testing
                tags: new[] { "udp", "512b", "offset", "xor", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped with phase shift"
            ),
            new GenericVerticalStripeTest(
                id: 40,
                namePrefix: "D",
                size: 1024,
                seed: 0xDE17A26u,
                changeSize: 3,
                unchangedSize: 13,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 2,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 297, // Placeholder; scaled intervals for higher-level pattern nesting
                tags: new[] { "udp", "1024b", "scaled", "xor", "motif" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped with scaled intervals"
            ),
            new GenericVerticalStripeTest(
                id: 41,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A27u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)Math.Clamp(b + 10, 0, 255),
                expectedDeltaSize: 201, // Placeholder; clamped addition for pending arithmetic/planar testing
                tags: new[] { "udp", "512b", "add", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths clamped-add modified vertical"
            ),
            new GenericVerticalStripeTest(
                id: 42,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A28u,
                changeSize: 2,
                unchangedSize: 14,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b << 1),
                expectedDeltaSize: 201, // Placeholder; bit-shift for pattern diversity in motif hashing
                tags: new[] { "udp", "512b", "shift", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths left-shifted vertical"
            ),
            new GenericVerticalStripeTest(
                id: 43,
                namePrefix: "D",
                size: 512,
                seed: 0xDE17A29u,
                changeSize: 8,
                unchangedSize: 8,
                extraChangeSize: 0,
                extraUnchangedSize: 0,
                offset: 0,
                intervalMultiplier: 1,
                modifier: b => (byte)(b ^ 0xFF),
                expectedDeltaSize: 329, // Placeholder; denser pattern to test compression threshold/fallback
                tags: new[] { "udp", "512b", "dense", "xor", "vertical" },
                descriptionTemplate: "{size}B packet: {change}/{total}ths {op}-flipped dense vertical"
            ),
            // TASK-0405: near-threshold boundary vectors bracketing newLen×1.5 FullReplace fallback
            new Test044_ThresholdBoundary_BelowRLE(),
            new Test045_ThresholdBoundary_AboveFullReplace(),
        };

        var manifest = new List<ManifestEntry>();
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
            var options = new DeltaZor.DeltaOptions();
            var delta = DeltaZor.CreateDelta(initial.Span, next.Span, options, out var stats);
            File.WriteAllBytes(deltaPath, delta);

            // === Generate Markdown ===
            var md = new StringBuilder();
            md.AppendLine($"# Test {t.Id:000}: {t.Name}");
            md.AppendLine();
            md.AppendLine($"**Tags:** `{string.Join("`, `", t.Tags)}`");
            md.AppendLine();
            md.AppendLine($"**Sizes:** initial: `{initial.Length}`, increment: `{next.Length}` bytes");
            md.AppendLine();
            md.AppendLine($"**DeltaZor:**  `{delta.Length}` bytes");
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
            md.AppendLine("<td><strong>DeltaZor Patch</strong></td>");
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
                md.AppendLine(DeltaDump.ColoredDeltaDump(delta.AsSpan()));
                md.AppendLine("</code></pre></td>");
            }
            else
            {
                SaveAsGrayscalePng(initial.Span, prevImgPath);
                SaveAsGrayscalePng(next.Span, nextImgPath);

                md.AppendLine($"<td><img src=\"{Path.GetFileName(prevImgPath)}\" width=\"400\"></td>");
                md.AppendLine($"<td><img src=\"{Path.GetFileName(nextImgPath)}\" width=\"400\"></td>");
                if (delta.Length < 1024)
                {
                    md.AppendLine("<td><pre><code class=\"language-hex\">");
                    md.AppendLine(DeltaDump.ColoredDeltaDump(delta.AsSpan()));
                    md.AppendLine("</code></pre></td>");
                }
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
    
            var entry = new ManifestEntry
            {
                TestId = t.Id,
                Name = t.Name.Replace("_", " "),
                Description = t.Description,
                BaseFile = Path.GetFileName(prevPath),
                NextFile = Path.GetFileName(nextPath),
                DeltaFile = Path.GetFileName(deltaPath),
                BaseSize = initial.Length,
                NextSize = next.Length,
                DeltaSize = delta.Length,
                BaseChecksum = ComputeSha256(prevPath),
                NextChecksum = ComputeSha256(nextPath),
                DeltaChecksum = ComputeSha256(deltaPath),
                Tags = t.Tags,
                Category = DetermineCategory(t.Tags),
                CompressionRatio = next.Length > 0 ? (double)delta.Length / next.Length : 0.0,
                GeneratedAt = DateTime.UtcNow,
                Version = "1.0"
            };

            manifest.Add(entry);
        }

// Create manifest with metadata
        var manifestData = new TestDataManifest
        {
            Version = "1.0",
            GeneratedAt = DateTime.UtcNow,
            TotalTests = manifest.Count,
            TotalSize = manifest.Sum(t => t.BaseSize + t.NextSize + t.DeltaSize),
            Tests = manifest,
            Checksum = "" // Will compute later if needed
        };

// Write manifest
        File.WriteAllText(
            Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifestData, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
        );

// Write checksums
        var checksums = manifest
            .SelectMany(m => new[]
            {
                $"SHA256({m.BaseFile})= {m.BaseChecksum}",
                $"SHA256({m.NextFile})= {m.NextChecksum}",
                $"SHA256({m.DeltaFile})= {m.DeltaChecksum}"
            });
        File.WriteAllLines(Path.Combine(outDir, "checksums.txt"), checksums);

        Console.WriteLine($"Generated {tests.Length} test cases in {outDir}");

        static string DetermineCategory(string[] tags)
        {
            if (tags.Contains("random")) return "Random";
            if (tags.Contains("sparse")) return "Sparse";
            if (tags.Contains("mixed")) return "Mixed";
            if (tags.Contains("rle-ideal")) return "RleIdeal";
            if (tags.Contains("udp")) return "UDP";
            if (tags.Contains("large")) return "Large";
            return "Other";
        }

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
    }
}