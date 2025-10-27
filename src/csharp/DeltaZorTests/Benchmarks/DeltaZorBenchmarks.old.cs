using System;
using System.Buffers;
using BenchmarkDotNet.Attributes;
using DZ;

namespace DZ.Tests
{
    [MemoryDiagnoser]
    [GcServer(true)]
    [MinColumn, MaxColumn, MeanColumn, MedianColumn]
    public class DeltaZorBenchmarks
    {
        private DeltaZor.DeltaOptions _defaultOptions;
        private DeltaZor.DeltaOptions _simdOptions;
        private IMemoryOwner<byte> _sharedMemory;
        private IMemoryOwner<byte> _randomData;
        private Memory<byte>[] _randomSpans;
        private Memory<byte>[] _mixedSpans;
        private Memory<byte>[] _sparseSpans;
        private const int SharedMemorySize = 32 * 1024 * 1024; // 32 MB
        private const int RandomDataSize = 32 * 1024 * 1024; // 32 MB
        private const int PatternCount = 100; // Number of pre-computed patterns
        private const int RandomSegmentSize = 1024; // Size of each random segment
        private int _patternIndex;

        [GlobalSetup]
        public void Setup()
        {
            // Initialize memory pools
            _sharedMemory = MemoryPool<byte>.Shared.Rent(SharedMemorySize);
            _randomData = MemoryPool<byte>.Shared.Rent(RandomDataSize);
            
            // Fill random data buffer once
            var random = new Random(42);
            random.NextBytes(_randomData.Memory.Span);
            
            // Pre-compute all data patterns
            PrecomputePatterns();
            
            // Initialize options
            _defaultOptions = new DeltaZor.DeltaOptions
            {
                CompressionThreshold = 0.5,
                EnableChecksum = true,
                MaxStackBufferSize = 4096
            };

            _simdOptions = new DeltaZor.DeltaOptions
            {
                CompressionThreshold = 0.5,
                EnableChecksum = true,
                MaxStackBufferSize = 4096
            };
        }

        private void PrecomputePatterns()
        {
            _randomSpans = new Memory<byte>[PatternCount];
            _mixedSpans = new Memory<byte>[PatternCount];
            _sparseSpans = new Memory<byte>[PatternCount];

            var randomDataMemory = _randomData.Memory;
            var tempMemory = _sharedMemory.Memory;

            for (int i = 0; i < PatternCount; i++)
            {
                int offset = i * RandomSegmentSize * 3; // Each pattern needs 3 segments (old, new, output)
                
                // Random base data
                _randomSpans[i] = randomDataMemory.Slice(offset, RandomSegmentSize);
                
                // Mixed pattern: copy base and XOR every other byte
                var mixed = tempMemory.Slice(offset, RandomSegmentSize);
                _randomSpans[i].Span.CopyTo(mixed.Span);
                for (int j = 0; j < RandomSegmentSize / 2; j += 2)
                    mixed.Span[j] ^= 0xFF;
                _mixedSpans[i] = mixed;
                
                // Sparse pattern: copy base and change first 102 bytes
                var sparse = tempMemory.Slice(offset + RandomSegmentSize, RandomSegmentSize);
                _randomSpans[i].Span.CopyTo(sparse.Span);
                for (int j = 0; j < 102; j++)
                    sparse.Span[j] = (byte)((offset + j) % 256);
                _sparseSpans[i] = sparse;
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _sharedMemory?.Dispose();
            _randomData?.Dispose();
        }

        [IterationSetup]
        public void IterationSetup()
        {
            _patternIndex = 0; // Reset pattern index for each iteration
        }

        private Span<byte> GetNextRandomSpan()
        {
            var memory = _randomSpans[_patternIndex % PatternCount];
            _patternIndex++;
            return memory.Span;
        }

        private Span<byte> GetNextMixedSpan()
        {
            var memory = _mixedSpans[_patternIndex % PatternCount];
            _patternIndex++;
            return memory.Span;
        }

        private Span<byte> GetNextSparseSpan()
        {
            var memory = _sparseSpans[_patternIndex % PatternCount];
            _patternIndex++;
            return memory.Span;
        }

        private Span<byte> AllocateOutputSpan(int size)
        {
            // Use shared memory for outputs, cycling through different sections
            int outputOffset = (_patternIndex % 100) * 4096; // 4KB per output slot
            return _sharedMemory.Memory.Span.Slice(outputOffset, size);
        }

        #region Creation Benchmarks - Zero Allocation

        [Benchmark(Baseline = true, Description = "1KB Random Creation")]
        public int CreateDelta_1KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, newData, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten; // Return size for validation
        }

        [Benchmark(Description = "1KB Identical Creation")]
        public int CreateDelta_1KB_Identical()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> identical = oldData; // Use the same data for identical case
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, identical, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "1KB Mixed Creation")]
        public int CreateDelta_1KB_Mixed()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> mixed = GetNextMixedSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, mixed, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "1KB Sparse Creation")]
        public int CreateDelta_1KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, sparse, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "10KB Random Creation")]
        public int CreateDelta_10KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> output = AllocateOutputSpan(20 * 1024);
            bool success = DeltaZor.CreateDelta(oldData, newData, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "10KB Mixed Creation")]
        public int CreateDelta_10KB_Mixed()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> mixed = GetNextMixedSpan();
            Span<byte> output = AllocateOutputSpan(20 * 1024);
            bool success = DeltaZor.CreateDelta(oldData, mixed, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "10KB Sparse Creation")]
        public int CreateDelta_10KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> output = AllocateOutputSpan(20 * 1024);
            bool success = DeltaZor.CreateDelta(oldData, sparse, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "100KB Random Creation")]
        public int CreateDelta_100KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> output = AllocateOutputSpan(200 * 1024);
            bool success = DeltaZor.CreateDelta(oldData, newData, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "1KB to 2KB Extension Creation")]
        public int CreateDelta_LengthChange_1KB_to_2KB()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> extended = AllocateOutputSpan(2048);
            oldData.CopyTo(extended);
            Span<byte> additionalData = GetNextRandomSpan();
            additionalData.CopyTo(extended.Slice(1024, 1024));
            Span<byte> output = AllocateOutputSpan(4096);
            bool success = DeltaZor.CreateDelta(oldData, extended, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        #endregion

        #region Application Benchmarks - Zero Allocation

        [Benchmark(Description = "Apply 1KB Random Delta")]
        public bool ApplyDelta_1KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 1KB Identical Delta")]
        public bool ApplyDelta_1KB_Identical()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> identical = oldData; // Use the same data for identical case
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, identical, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 1KB Mixed Delta")]
        public bool ApplyDelta_1KB_Mixed()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> mixed = GetNextMixedSpan();
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, mixed, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 1KB Sparse Delta")]
        public bool ApplyDelta_1KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, sparse, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 10KB Random Delta")]
        public bool ApplyDelta_10KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(20 * 1024);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(10 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 10KB Mixed Delta")]
        public bool ApplyDelta_10KB_Mixed()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> mixed = GetNextMixedSpan();
            Span<byte> delta = AllocateOutputSpan(20 * 1024);
            DeltaZor.CreateDelta(oldData, mixed, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(10 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 10KB Sparse Delta")]
        public bool ApplyDelta_10KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> delta = AllocateOutputSpan(20 * 1024);
            DeltaZor.CreateDelta(oldData, sparse, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(10 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply 100KB Random Delta")]
        public bool ApplyDelta_100KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(200 * 1024);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(100 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Apply Length Change Delta (1KB to 2KB)")]
        public bool ApplyDelta_LengthChange_1KB_to_2KB()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> extended = AllocateOutputSpan(2048);
            oldData.CopyTo(extended);
            Span<byte> additionalData = GetNextRandomSpan();
            additionalData.CopyTo(extended.Slice(1024, 1024));
            Span<byte> delta = AllocateOutputSpan(4096);
            DeltaZor.CreateDelta(oldData, extended, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(2048);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        #endregion

        #region Roundtrip Benchmarks - Zero Allocation (Create + Apply)

        [Benchmark(Description = "Roundtrip 1KB Random")]
        public bool Roundtrip_1KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Roundtrip 1KB Identical")]
        public bool Roundtrip_1KB_Identical()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> identical = oldData; // Use the same data for identical case
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, identical, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Roundtrip 1KB Mixed")]
        public bool Roundtrip_1KB_Mixed()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> mixed = GetNextMixedSpan();
            Span<byte> delta = AllocateOutputSpan(2048);
            DeltaZor.CreateDelta(oldData, mixed, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Roundtrip 10KB Random")]
        public bool Roundtrip_10KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(20 * 1024);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(10 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Roundtrip 10KB Sparse")]
        public bool Roundtrip_10KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> delta = AllocateOutputSpan(20 * 1024);
            DeltaZor.CreateDelta(oldData, sparse, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(10 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        [Benchmark(Description = "Roundtrip 100KB Random")]
        public bool Roundtrip_100KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> delta = AllocateOutputSpan(200 * 1024);
            DeltaZor.CreateDelta(oldData, newData, delta, out int deltaSize, _defaultOptions, out var stats);
            Span<byte> output = AllocateOutputSpan(100 * 1024);
            var result = DeltaZor.ApplyDelta(oldData, delta.Slice(0, deltaSize), output, out _);
            return result.Value;
        }

        #endregion

        #region Compression Analysis - Zero Allocation (Pre-allocated scratch)

        [Benchmark(Description = "Analyze 1KB Random")]
        public DeltaZor.DeltaStats Analyze_1KB_Random()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            return DeltaZor.AnalyzeDelta(oldData, newData);
        }

        [Benchmark(Description = "Analyze 1KB Sparse")]
        public DeltaZor.DeltaStats Analyze_1KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            return DeltaZor.AnalyzeDelta(oldData, sparse);
        }

        [Benchmark(Description = "Analyze 10KB Sparse")]
        public DeltaZor.DeltaStats Analyze_10KB_Sparse()
        {
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            return DeltaZor.AnalyzeDelta(oldData, sparse);
        }

        #endregion

        #region Configuration Variations - Zero Allocation

        [Benchmark(Description = "Create 1KB Random No Checksum")]
        public int CreateDelta_NoChecksum()
        {
            var options = new DeltaZor.DeltaOptions
            {
                EnableChecksum = false,
                CompressionThreshold = _defaultOptions.CompressionThreshold
            };
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> newData = GetNextRandomSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, newData, output, out int bytesWritten, options, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "Create 1KB Aggressive RLE")]
        public int CreateDelta_AggressiveRLE()
        {
            var options = new DeltaZor.DeltaOptions
            {
                CompressionThreshold = 0.1,
                EnableChecksum = _defaultOptions.EnableChecksum
            };
            Span<byte> oldData = GetNextRandomSpan();
            Span<byte> sparse = GetNextSparseSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldData, sparse, output, out int bytesWritten, options, out var stats);
            return bytesWritten;
        }

        #endregion

        #region DragonHunter-Specific - Zero Allocation

        [Benchmark(Description = "Entity 81B Mixed")]
        public int EntityComponentDelta_81B_Mixed()
        {
            Span<byte> oldComponent = GetNextRandomSpan().Slice(0, 81);
            Span<byte> newComponent = GetNextMixedSpan().Slice(0, 81);
            Span<byte> output = AllocateOutputSpan(162);
            bool success = DeltaZor.CreateDelta(oldComponent, newComponent, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "Terrain 1KB Sparse")]
        public int TerrainTileDelta_1KB_Sparse()
        {
            Span<byte> oldTile = GetNextRandomSpan();
            Span<byte> newTile = GetNextSparseSpan();
            Span<byte> output = AllocateOutputSpan(2048);
            bool success = DeltaZor.CreateDelta(oldTile, newTile, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        [Benchmark(Description = "Batch 10KB Mixed")]
        public int BatchEntitySync_10KB_Mixed()
        {
            Span<byte> oldBatch = GetNextRandomSpan();
            Span<byte> newBatch = GetNextMixedSpan();
            Span<byte> output = AllocateOutputSpan(20 * 1024);
            bool success = DeltaZor.CreateDelta(oldBatch, newBatch, output, out int bytesWritten, _defaultOptions, out var stats);
            return bytesWritten;
        }

        #endregion
    }
}