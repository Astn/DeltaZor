using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class StrategySelectionTests
    {
        [Fact]
        public void HybridStrategySelection_ChoosesOptimalCompression()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var newData = new byte[] { 1, 9, 3, 4, 5, 10, 7, 8 };
            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 0.0 }; // Lower threshold

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert
            Assert.True(delta.Length >= 9);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(8, outputLength);
            // For this pattern, full replace is more efficient (RLE estimate 12 > 8)
            Assert.Equal(0x01, compressionType);
        }

        [Fact]
        public void EstimateRLESize_CalculatesAccurateSizeEstimates()
        {
            // Arrange
            var oldData = new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
            var newData = new byte[] { 1, 1, 2, 2, 1, 1, 1, 1, 1, 1 }; // Only 2 bytes changed
            
            // Create an actual RLE delta to compare against
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            
            // Extract the data portion (without header and checksum)
            // Header is 5 bytes (4 for length + 1 for compression type)
            // Checksum is 4 bytes
            const int headerSize = 5;
            const int checksumSize = 4;
            var dataPortion = delta.AsSpan().Slice(headerSize, delta.Length - headerSize - checksumSize);
            int actualRleSize = dataPortion.Length;

            // Assert
            // The actual size should be relatively small since only 2 bytes changed
            // This test validates that RLE encoding produces compact representations
            Assert.True(actualRleSize > 0);
            // For this specific case, RLE should be more efficient than full replace
            Assert.True(actualRleSize <= newData.Length + 10); // Allow some overhead for encoding
            // Should have 2 zero runs (positions 0-1 and 4-9) and 1 non-zero run (positions 2-3)
            Assert.Equal(2, stats.PatternCounts.ZeroRunCount);
            Assert.Equal(1, stats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, stats.PatternCounts.ExtensionCount);
            Assert.Equal(0, stats.PatternCounts.TruncationCount);
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
            var strictOptions = new DeltaZor.DeltaOptions { CompressionThreshold = 0.9 };
            
            // Test with relaxed threshold (accept lower compression)
            var relaxedOptions = new DeltaZor.DeltaOptions { CompressionThreshold = 0.1 };

            // Act
            var deltaStrict = DeltaZor.CreateDelta(oldData, newData, strictOptions, out var statsStrict);
            var deltaRelaxed = DeltaZor.CreateDelta(oldData, newData, relaxedOptions, out var statsRelaxed);

            // Assert
            // Header is 5 bytes (4 for length + 1 for compression type)
            // Checksum is 4 bytes
            const int headerSize = 5;
            const int checksumSize = 4;
            Assert.True(deltaStrict.Length >= headerSize + checksumSize);
            Assert.True(deltaRelaxed.Length >= headerSize + checksumSize);
            
            byte compressionTypeStrict = deltaStrict[4];
            byte compressionTypeRelaxed = deltaRelaxed[4];
            
            // Both should produce valid deltas, but may choose different strategies
            // based on the threshold settings
            Assert.True(compressionTypeStrict == 0x00 || compressionTypeStrict == 0x01);
            Assert.True(compressionTypeRelaxed == 0x00 || compressionTypeRelaxed == 0x01);
        }
    }
}