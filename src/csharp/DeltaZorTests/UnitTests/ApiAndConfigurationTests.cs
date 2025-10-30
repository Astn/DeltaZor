using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class ApiAndConfigurationTests
    {
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