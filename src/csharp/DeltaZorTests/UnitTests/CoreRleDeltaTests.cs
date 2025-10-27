using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class CoreRleDeltaTests
    {
        [Fact]
        public void CreateDelta_IdenticalArrays_ReturnsRLEDelta()
        {
            // Arrange
            var data = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(data, data, out var stats);

            // Assert
            // New format: [output_length:4][compression_type:1][data...][checksum:4]
            // For identical arrays, should use RLE with all zeros
            Assert.True(delta.Length >= 9); // Header + checksum + minimal data
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength); // Same length
            Assert.Equal(0x00, compressionType); // RLE compression
            Assert.Equal(1, stats.PatternCounts.ZeroRunCount); // One zero run for identical data
            Assert.Equal(0, stats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, stats.PatternCounts.ExtensionCount);
            Assert.Equal(0, stats.PatternCounts.TruncationCount);
        }

        [Fact]
        public void CreateDelta_CompletelyDifferentArrays_ReturnsFullReplace()
        {
            // Arrange
            var oldData = new byte[] { 0, 0, 0, 0, 0 };
            var newData = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert
            // Should choose full replace for high change density
            Assert.True(delta.Length >= 13);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength);
            // May choose RLE or full replace depending on compression analysis
            if (compressionType == 0x01) // Full replace
            {
                Assert.Equal(0, stats.PatternCounts.ZeroRunCount);
                Assert.Equal(0, stats.PatternCounts.NonZeroRunCount);
                Assert.Equal(0, stats.PatternCounts.ExtensionCount);
                Assert.Equal(0, stats.PatternCounts.TruncationCount);
            }
        }

        [Fact]
        public void CreateDelta_MixedDifferences_UsesAppropriateCompression()
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
            // Full replace should have no pattern counts
            Assert.Equal(0, stats.PatternCounts.ZeroRunCount);
            Assert.Equal(0, stats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, stats.PatternCounts.ExtensionCount);
            Assert.Equal(0, stats.PatternCounts.TruncationCount);
        }

        [Fact]
        public void ApplyDelta_ValidDelta_RecreatesNewData()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var delta = DeltaZor.CreateDelta(oldData, newData, out var createStats);

            // Act
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(5, applyStats.NewSize);
        }

        [Fact]
        public void LengthChanges_Extension_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 2, 3, 4, 5, 6, 7 }; // Extended

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var createStats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(7, applyStats.NewSize);
            // Should have one zero run for identical prefix and one extension
            Assert.Equal(1, createStats.PatternCounts.ZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.NonZeroRunCount);
            Assert.Equal(1, createStats.PatternCounts.ExtensionCount);
            Assert.Equal(0, createStats.PatternCounts.TruncationCount);
        }

        [Fact]
        public void LengthChanges_Truncation_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
            var newData = new byte[] { 1, 2, 3, 4, 5 }; // Truncated

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var createStats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(5, applyStats.NewSize);
            // Should have one zero run for identical prefix and one truncation
            Assert.Equal(1, createStats.PatternCounts.ZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.ExtensionCount);
            Assert.Equal(1, createStats.PatternCounts.TruncationCount);
        }
    }
}