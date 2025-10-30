using DZ.Shared;

namespace DZ.Tests.UnitTests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using DZ; // Assuming DeltaZor namespace

public class DeltaFileValidationTests
{
    private static readonly DeltaZor.DeltaOptions DefaultOptions = new DeltaZor.DeltaOptions
    {
        UseSIMD = true,
        EnableMotifDetection = true,
        EnableChecksum = true,
        CompressionThreshold = 0.5
    };

    private static string ComputeSha256(byte[] data)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public static void GenerateTestData()
    {
        if (ranGenerateTestData < DateTime.Now.Subtract(TimeSpan.FromMinutes(10)))
        {
            DZ.TestGen.Program.Main([]);
            ranGenerateTestData = DateTime.Now;
        }
        
    }
    static DateTime ranGenerateTestData = DateTime.Today;
    
    public static IEnumerable<object[]> GetDeltaTestData()
    {
        GenerateTestData();
        
        string baseDir = AppContext.BaseDirectory;
        string manifestPath = Path.Combine(baseDir, "testdata", "manifest.json");
        
        if (!File.Exists(manifestPath))
        {
            yield break; // No tests if manifest missing
        }

        string json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<TestDataManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (manifest == null || manifest.Tests == null)
        {
            yield break;
        }

        foreach (var entry in manifest.Tests)
        {
            if (entry.IsValid) // Using the IsValid from ManifestEntry
            {
                yield return new object[] { entry.TestId.ToString("000"), entry.Name, entry };
            }
        }
    }

    
    
    [Theory]
    [MemberData(nameof(GetDeltaTestData))]
    public void ValidateTestGenSamples(string id, string name, ManifestEntry entry)
    {
        string baseDir = AppContext.BaseDirectory;
        string testDataDir = Path.Combine(baseDir, "testdata");
        
        string basePath = Path.Combine(testDataDir, entry.BaseFile);
        string nextPath = Path.Combine(testDataDir, entry.NextFile);
        string deltaPath = Path.Combine(testDataDir, entry.DeltaFile);

        // Load and verify files with checksums and sizes
        byte[] baseBytes = File.ReadAllBytes(basePath);
        Assert.Equal(entry.BaseSize, baseBytes.Length);
        Assert.Equal(entry.BaseChecksum.ToLowerInvariant(), ComputeSha256(baseBytes));

        byte[] nextBytes = File.ReadAllBytes(nextPath);
        Assert.Equal(entry.NextSize, nextBytes.Length);
        Assert.Equal(entry.NextChecksum.ToLowerInvariant(), ComputeSha256(nextBytes));

        byte[] expectedDeltaBytes = File.ReadAllBytes(deltaPath);
        Assert.Equal(entry.DeltaSize, expectedDeltaBytes.Length);
        Assert.Equal(entry.DeltaChecksum.ToLowerInvariant(), ComputeSha256(expectedDeltaBytes));

        ReadOnlySpan<byte> oldData = baseBytes;
        ReadOnlySpan<byte> newData = nextBytes;
        ReadOnlySpan<byte> expectedDelta = expectedDeltaBytes;

        // Test 1: Create delta and compare to expected
        byte[] computedDeltaBytes = DeltaZor.CreateDelta(oldData, newData, DefaultOptions, out var stats);
        ReadOnlySpan<byte> computedDelta = computedDeltaBytes;

        Assert.True(computedDelta.SequenceEqual(expectedDelta),
            $"Delta mismatch for test '{entry.Name}' (ID: {entry.TestId}, Category: {entry.Category}). " +
            $"Computed size: {computedDelta.Length}, Expected: {expectedDelta.Length}. " +
            $"Patterns: Zero={stats.OpCodeCounts.ZeroRunCount}, NonZero={stats.OpCodeCounts.NonZeroRunCount}, " +
            $"UniformMotif={stats.OpCodeCounts.UniformMotifCount}, VaryingMotif={stats.OpCodeCounts.VaryingMotifCount}, " +
            $"AvgDensity={stats.OpCodeCounts.AverageMaskDensity:P2}. " +
            $"Tags: {string.Join(", ", entry.Tags)}");

        // Test 2: Apply delta and verify output matches next
        byte[] outputBytes = new byte[(int)entry.NextSize];
        Span<byte> output = outputBytes;

        var applyResult = DeltaZor.ApplyDelta(oldData, computedDelta, output, out var applyStats);
        Assert.True(applyResult.Success, $"Delta application failed for test '{entry.Name}'.");
        Assert.True(output.SequenceEqual(newData),
            $"Applied output mismatch for test '{entry.Name}'. " +
            $"Output length: {output.Length}, Expected: {newData.Length}");

        // Optional: Validate compression stats
        double computedRatio = newData.Length > 0 ? (double)computedDelta.Length / newData.Length : 0.0;
        Assert.InRange(computedRatio, 0.0, entry.CompressionRatio * 1.05); // Allow 5% variance if needed

        // If DeltaStats available
        // DeltaStats stats = ...;
        // Assert.True(stats.UsedRLE || stats.CompressionType == "FullReplace", "Unexpected compression type.");
    }
}
