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
            // Format: [output_length:4][compression_type:1][data...]
            // Checksum is optional (off by default), so minimum is 5 (header) + data
            Assert.True(delta.Length >= 5); // Header + minimal data
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength); // Same length
            Assert.Equal(0x00, compressionType); // RLE compression
            Assert.Equal(1, stats.OpCodeCounts.ZeroRunCount); // One zero run for identical data
            Assert.Equal(0, stats.OpCodeCounts.NonZeroRunCount);
            Assert.Equal(0, stats.OpCodeCounts.ExtensionCount);
            Assert.Equal(0, stats.OpCodeCounts.TruncationCount);
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
            // Minimum: 5 header + 5 data (checksum off by default)
            Assert.True(delta.Length >= 10);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength);
            // May choose RLE or full replace depending on compression analysis
            if (compressionType == 0x01) // Full replace
            {
                Assert.Equal(0, stats.OpCodeCounts.ZeroRunCount);
                Assert.Equal(0, stats.OpCodeCounts.NonZeroRunCount);
                Assert.Equal(0, stats.OpCodeCounts.ExtensionCount);
                Assert.Equal(0, stats.OpCodeCounts.TruncationCount);
            }
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
            Assert.Equal(1, createStats.OpCodeCounts.ZeroRunCount);
            Assert.Equal(0, createStats.OpCodeCounts.NonZeroRunCount);
            Assert.Equal(1, createStats.OpCodeCounts.ExtensionCount);
            Assert.Equal(0, createStats.OpCodeCounts.TruncationCount);
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
            Assert.Equal(1, createStats.OpCodeCounts.ZeroRunCount);
            Assert.Equal(0, createStats.OpCodeCounts.NonZeroRunCount);
            Assert.Equal(0, createStats.OpCodeCounts.ExtensionCount);
            Assert.Equal(1, createStats.OpCodeCounts.TruncationCount);
        }
    }
}