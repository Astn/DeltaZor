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
            // Smaller than the 5-byte header alone, so it cannot fit ANY mode's output. (Was 10:
            // under auto-mode best-of (TASK-0366) this 5-byte input now correctly picks the smaller
            // FullReplace candidate — 5 header + 5 raw = 10 bytes — which would FIT a 10-byte
            // buffer, so 10 no longer exercises the too-small path. 4 < header is unconditionally
            // too small.)
            var smallBuffer = new byte[4]; // Too small for even the header

            // Act
            var success = DeltaZor.CreateDelta(oldData.AsSpan(), newData.AsSpan(), smallBuffer.AsSpan(), out int requiredSize, out var stats);

            // Assert
            Assert.False(success);
            Assert.True(requiredSize > smallBuffer.Length);
        }
    }
}