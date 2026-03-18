using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using DZ;
using System;
using System.Buffers;
using System.Threading;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using System.Runtime.CompilerServices;
using DZ.Shared;

namespace DeltaZorTests.Benchmarks;

public class TestData
{
    public byte[] PrevBytes { get; set; } = Array.Empty<byte>();
    public byte[] NextBytes { get; set; } = Array.Empty<byte>();
    public byte[] DeltaBytes { get; set; } = Array.Empty<byte>();
    public ManifestEntry Metadata { get; set; }
    
    public ReadOnlySpan<byte> Prev => PrevBytes.AsSpan();
    public ReadOnlySpan<byte> Next => NextBytes.AsSpan();
    public ReadOnlySpan<byte> ExpectedDelta => DeltaBytes.AsSpan();
}
[MemoryDiagnoser]
[GcServer(true)]
public class DeltaZorTestDataBenchmarks
{
    private IMemoryOwner<byte> _sharedOutputMemory;
    private int _outputIndex;
    private static readonly object _indexLock = new object();
    private const int OutputSlots = 100;
    private const int SlotSize = 8192;
    private const int LargeBufferThreshold = 100_000; // 100KB threshold for direct allocation

    private static readonly Dictionary<string, TestData> _testData;
    private static readonly List<ManifestEntry> _testCases;
    private static readonly string _testDataDir;

static DeltaZorTestDataBenchmarks()
{
    // Load manifest and data at class initialization (before any benchmark runs)
    var baseDir = AppContext.BaseDirectory;
    _testDataDir = FindTestDataDirectory(baseDir);

    var manifestPath = Path.Combine(_testDataDir, "manifest.json");

    if (!File.Exists(manifestPath))
        throw new FileNotFoundException($"Run DeltaZor.TestGen first: {manifestPath}");

    var json = File.ReadAllText(manifestPath);
    var manifest = JsonSerializer.Deserialize<TestDataManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("Invalid manifest.json");

    _testCases = manifest.Tests ?? throw new InvalidDataException("No tests in manifest");
    Console.WriteLine($"Loaded {manifest.Tests.Count} tests");
    _testData = new Dictionary<string, TestData>(StringComparer.OrdinalIgnoreCase);

    foreach (var test in _testCases)
    {
        try
        {
            var prevPath = Path.Combine(_testDataDir, test.BaseFile);
            var nextPath = Path.Combine(_testDataDir, test.NextFile);
            var deltaPath = Path.Combine(_testDataDir, test.DeltaFile);

            // Validate file existence
            if (!File.Exists(prevPath))
                throw new FileNotFoundException($"Previous data file not found: {prevPath}");
            if (!File.Exists(nextPath))
                throw new FileNotFoundException($"Next data file not found: {nextPath}");

            Console.WriteLine($"Loading test {test.TestId:000}: prev={prevPath}, next={nextPath}");
            Console.WriteLine($"Checksums - prev: '{test.BaseChecksum}', next: '{test.NextChecksum}', delta: '{test.DeltaChecksum}'");

            var prevBytes = File.ReadAllBytes(prevPath);
            var nextBytes = File.ReadAllBytes(nextPath);
            var deltaBytes = File.Exists(deltaPath) ? File.ReadAllBytes(deltaPath) : Array.Empty<byte>();

            // Validate checksums
            ValidateChecksum(prevBytes, test.BaseChecksum, $"{test.TestId:000}.prev");
            ValidateChecksum(nextBytes, test.NextChecksum, $"{test.TestId:000}.next");
            if (deltaBytes.Length > 0) 
                ValidateChecksum(deltaBytes, test.DeltaChecksum, $"{test.TestId:000}.delta");

            // Validate sizes
            if (prevBytes.Length != test.BaseSize)
                throw new InvalidDataException($"Size mismatch for {test.TestId:000}: expected {test.BaseSize}, got {prevBytes.Length} for previous data");
            if (nextBytes.Length != test.NextSize)
                throw new InvalidDataException($"Size mismatch for {test.TestId:000}: expected {test.NextSize}, got {nextBytes.Length} for next data");

            _testData[test.TestId.ToString("000")] = new TestData
            {
                PrevBytes = prevBytes,
                NextBytes = nextBytes,
                DeltaBytes = deltaBytes,
                Metadata = test
            };
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to load test case {test.TestId:000}: {ex.Message}", ex);
        }
    }
}

    private static string FindTestDataDirectory(string startDir)
{
    // First try to find src directory and navigate to testdata
    var dir = startDir;
    string srcDir = null;
    while (dir != null)
    {
        if (Path.GetFileName(dir) == "src" || Directory.Exists(Path.Combine(dir, "src")))
        {
            srcDir = Directory.Exists(Path.Combine(dir, "src")) ? Path.Combine(dir, "src") : dir;
            break;
        }
        dir = Directory.GetParent(dir)?.FullName;
    }
    
    if (srcDir != null)
    {
        // Try common build paths relative to src
        string[] possiblePaths = {
            Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Debug", "net10.0", "testdata"),
            Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Release", "net10.0", "testdata"),
            Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Debug", "net8.0", "testdata"),
            Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Release", "net8.0", "testdata"),
            Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "testdata")
        };
        
        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path)) return path;
        }
    }
    
    // Fallback: original testdata search
    dir = startDir;
    while (dir != null)
    {
        var candidate = Path.Combine(dir, "testdata");
        if (Directory.Exists(candidate)) return candidate;
        dir = Directory.GetParent(dir)?.FullName;
    }
    
    throw new DirectoryNotFoundException("Could not find testdata/ directory");
}

    private static void ValidateChecksum(byte[] data, string expected, string name)
    {
        var actual = ComputeSha256(data);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA256 mismatch for {name}: expected {expected}, got {actual}");
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
    }

    private int GetNextOutputIndex()
    {
        lock (_indexLock)
            return _outputIndex++ % OutputSlots;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        int totalSize = OutputSlots * SlotSize;
        _sharedOutputMemory = MemoryPool<byte>.Shared.Rent(totalSize);
        _outputIndex = 0;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _sharedOutputMemory?.Dispose();
    }

    // ——— PARAMS ———
    public IEnumerable<string> AllTestIds => _testCases.Select(t => t.TestId.ToString("000"));

    // Test categories by tags
    public IEnumerable<string> RandomTests => _testCases.Where(t => t.Tags.Contains("random")).Select(t => t.TestId.ToString("000"));
    public IEnumerable<string> SparseTests => _testCases.Where(t => t.Tags.Contains("sparse")).Select(t => t.TestId.ToString("000"));
    public IEnumerable<string> MixedTests => _testCases.Where(t => t.Tags.Contains("mixed")).Select(t => t.TestId.ToString("000"));
    public IEnumerable<string> RleIdealTests => _testCases.Where(t => t.Tags.Contains("rle-ideal")).Select(t => t.TestId.ToString("000"));
    public IEnumerable<string> UdpTests => _testCases.Where(t => t.Tags.Contains("udp")).Select(t => t.TestId.ToString("000"));
    public IEnumerable<string> LargeDataTests => _testCases.Where(t => t.Tags.Contains("large")).Select(t => t.TestId.ToString("000"));

    // To run category-specific benchmarks, uncomment one of the following and comment out the AllTestIds param:
    // [ParamsSource(nameof(RandomTests))]
    // public string TestId { get; set; }

    // [ParamsSource(nameof(SparseTests))]
    // public string TestId { get; set; }

    // [ParamsSource(nameof(MixedTests))]
    // public string TestId { get; set; }


    [ParamsSource(nameof(AllTestIds))]
    public string TestId { get; set; } = "001";

    // ——— BENCHMARKS ———
    [Benchmark]
    public int Compress()
    {
        var test = _testData[TestId];
        // Estimate output size based on the larger of the two inputs plus some overhead
        int estimatedOutputSize = Math.Max(test.Prev.Length, test.Next.Length) + 1024;
        var outputSpan = GetOutputSpan(estimatedOutputSize);
        DeltaZor.CreateDelta(test.Prev, test.Next, outputSpan, out int written, out var stats);
        return written;
    }

    [Benchmark]
    public DeltaZor.DeltaStats CompressWithStats()
    {
        var test = _testData[TestId];
        return DeltaZor.AnalyzeDelta(test.Prev, test.Next);
    }

[Benchmark]
public bool Apply()
{
    var test = _testData[TestId];
    try
    {
        var resultSpan = GetOutputSpan(test.Next.Length);
        var result = DeltaZor.ApplyDelta(test.Prev, test.ExpectedDelta, resultSpan, out var stats);
        
        // Additional validation: check if the result matches expected
        if (result.Success)
        {
            // Compare the result with the expected next data
            bool dataMatches = resultSpan.SequenceEqual(test.Next);
            if (!dataMatches)
            {
                Console.WriteLine($"Apply produced incorrect result for {TestId}");
                return false;
            }
        }
        
        return result.Success;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Apply crashed for {TestId}: {ex.GetType().Name}: {ex.Message}");
        return false;
    }
}

[Benchmark]
public DeltaZor.DeltaStats ApplyWithStats()
{
    var test = _testData[TestId];
    var resultSpan = GetOutputSpan(test.Next.Length);
    var result = DeltaZor.ApplyDelta(test.Prev, test.ExpectedDelta, resultSpan, out var stats);
    return stats;
}

// ——— CATEGORY-SPECIFIC BENCHMARKS ———
// These benchmarks can be used to run specific categories of tests
// by changing the ParamsSource attribute

[Benchmark]
public int CompressRandomTests()
{
    // This will only run when RandomTestId is used as the parameter
    var test = _testData[TestId]; // Still using TestId for now
    int estimatedOutputSize = Math.Max(test.Prev.Length, test.Next.Length) + 1024;
    var outputSpan = GetOutputSpan(estimatedOutputSize);
    DeltaZor.CreateDelta(test.Prev, test.Next, outputSpan, out int written, out var stats);
    return written;
}

private Span<byte> GetOutputSpan(int size)
{
    // For large test cases, use direct allocation to avoid buffer pool limitations
    if (size > LargeBufferThreshold)
    {
        return new byte[size].AsSpan();
    }
    
    int slot = GetNextOutputIndex();
    return _sharedOutputMemory.Memory.Span.Slice(slot * SlotSize, size);
}

// NOTE: Parallel benchmark execution could be implemented by:
// 1. Using BenchmarkDotNet's [Group] and [GroupMember] attributes
// 2. Creating separate benchmark classes for parallel execution
// 3. Using concurrent data structures for shared resources
// 4. Implementing thread-safe versions of the GetOutputSpan method
}