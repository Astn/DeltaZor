using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class ApiAndConfigurationTests
    {
        [Fact]
        public void SpanBasedAPI_WorksCorrectly()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var output = new byte[newData.Length];

            // Act
            var delta = DeltaZor.CreateDelta(oldData.AsSpan(), newData.AsSpan(), out var createStats);
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output.AsSpan(0, newData.Length));
            Assert.Equal(5, stats.NewSize);
            // For this small pattern, full replace is optimal due to opcode overhead
            Assert.Equal(0, createStats.PatternCounts.ZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.ExtensionCount);
            Assert.Equal(0, createStats.PatternCounts.TruncationCount);
        }

        [Fact]
        public void DeltaOptions_ConfigurationWorks()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var createStats);
            var output = new byte[newData.Length+ 20];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output.AsSpan(0, newData.Length));
            // For this small pattern, full replace is optimal even with low threshold due to opcode overhead
            Assert.Equal(0, createStats.PatternCounts.ZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.NonZeroRunCount);
            Assert.Equal(0, createStats.PatternCounts.ExtensionCount);
            Assert.Equal(0, createStats.PatternCounts.TruncationCount);
        }

        [Fact]
        public void AnalyzeDelta_ProvidesCorrectStatistics()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 }; // 1 change out of 5

            // Act
            var stats = DeltaZor.AnalyzeDelta(oldData, newData);

            // Assert
            Assert.Equal(5, stats.OldSize);
            Assert.Equal(5, stats.NewSize);
            Assert.Equal(0.2, stats.ChangeDensity, 2); // 20% change density
        }

        [Fact]
        public void BufferManagement_SpanAPI_BufferTooSmall()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var smallBuffer = new byte[10]; // Too small

            // Act
            var success = DeltaZor.CreateDelta(oldData.AsSpan(), newData.AsSpan(), smallBuffer.AsSpan(), out int requiredSize, out var stats);

            // Assert
            Assert.False(success);
            Assert.True(requiredSize > smallBuffer.Length);
        }
    }
}