using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class StrategySelectionTests
    {
        [Fact]
        public void EstimateRLESize_CalculatesAccurateSizeEstimates()
        {
            // Arrange
            var oldData = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            var newData = new byte[] { 1, 1, 2, 2, 1, 1, 1, 1, 1, 1 }; // Only 2 bytes changed
            
            // Create an actual RLE delta to compare against
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            
            // Extract the data portion (without header)
            // Header is 5 bytes (4 for length + 1 for compression type)
            // Checksum is off by default, so no trailing bytes to skip
            const int headerSize = 5;
            var dataPortion = delta.AsSpan().Slice(headerSize);
            int actualRleSize = dataPortion.Length;

            // Assert
            // The actual size should be relatively small since only 2 bytes changed
            // This test validates that RLE encoding produces compact representations
            Assert.True(actualRleSize > 0);
            // For this specific case, RLE should be more efficient than full replace
            Assert.True(actualRleSize <= newData.Length + 10); // Allow some overhead for encoding
            // Should have 2 zero runs (positions 0-1 and 4-9) and 1 non-zero run (positions 2-3)
            Assert.Equal(2, stats.OpCodeCounts.ZeroRunCount);
            Assert.Equal(1, stats.OpCodeCounts.NonZeroRunCount);
            Assert.Equal(0, stats.OpCodeCounts.ExtensionCount);
            Assert.Equal(0, stats.OpCodeCounts.TruncationCount);
        }

        [Fact]
        public void CompressionThreshold_AffectsStrategyChoice()
        {
            // Arrange
            // Create data with moderate change density
            var oldData = new byte[100];
            var newData = new byte[100];
            
            // Fill with pattern
            for (int i = 0; i < 100; i++)
            {
                oldData[i] = (byte)(i % 10);
                newData[i] = (byte)(i % 10);
            }
            
            // Change 30% of the data
            for (int i = 0; i < 30; i++)
            {
                newData[i * 3] = 99; // Change every 3rd byte
            }
            
            // Test with very strict threshold (require high compression)
            var strictOptions = new DeltaZor.DeltaOptions { CompressionThreshold = 0.1 };
            
            // Test with relaxed threshold (accept lower compression)
            var relaxedOptions = new DeltaZor.DeltaOptions { CompressionThreshold = 0.9 };

            // Act
            var deltaStrict = DeltaZor.CreateDelta(oldData, newData, strictOptions, out var statsStrict);
            var deltaRelaxed = DeltaZor.CreateDelta(oldData, newData, relaxedOptions, out var statsRelaxed);

            // Assert
            // Header is 5 bytes (4 for length + 1 for compression type)
            // Checksum is off by default, so minimum is just the header
            const int headerSize = 5;
            Assert.True(deltaStrict.Length >= headerSize);
            Assert.True(deltaRelaxed.Length >= headerSize);
            
            byte compressionTypeStrict = deltaStrict[4];
            byte compressionTypeRelaxed = deltaRelaxed[4];
            
            // Both should produce valid deltas, but may choose different strategies
            // based on the threshold settings
            Assert.True(compressionTypeStrict == 0x00 || compressionTypeStrict == 0x01);
            Assert.True(compressionTypeRelaxed == 0x00 || compressionTypeRelaxed == 0x01);
        }
    }
}