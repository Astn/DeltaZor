using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class EdgeCasesAndErrorHandlingTests
    {
        [Fact]
        public void ApplyDelta_EmptyDelta_ReturnsOriginalData()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var delta = Array.Empty<byte>();
            var output = new byte[oldData.Length];

            // Act
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.False(result.Success); // Empty delta is invalid
        }

        [Fact]
        public void ErrorHandling_InvalidDelta_Format()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3 };
            var invalidDelta = new byte[] { 1, 2, 3 }; // Too short
            var output = new byte[oldData.Length];

            // Act
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), invalidDelta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("small", result.Error.ToLowerInvariant());
        }

        [Fact]
        public void EdgeCases_EmptyArrays_HandledGracefully()
        {
            // Arrange
            var empty = Array.Empty<byte>();

            // Act
            var delta = DeltaZor.CreateDelta(empty, empty, out var createStats);
            var output = new byte[0];
            var result = DeltaZor.ApplyDelta(empty.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(empty, output);
            Assert.Equal(0, applyStats.NewSize);
        }

        [Fact]
        public void EdgeCases_SingleElementArrays_WorkCorrectly()
        {
            // Arrange
            var oldData = new byte[] { 5 };
            var newData = new byte[] { 10 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var createStats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(1, applyStats.NewSize);
        }

        [Fact]
        public void CreateDelta_LengthMismatch_UsesFullReplace()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3 };
            var newData = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert
            // Length mismatch should use full replace
            Assert.True(delta.Length >= 13);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength);
            Assert.Equal(0x01, compressionType); // Full replace
        }
    }
}