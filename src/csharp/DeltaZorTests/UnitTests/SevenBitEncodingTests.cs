using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class SevenBitEncodingTests
    {
        [Fact]
        public void SevenBitEncoding_LargeNumbers_Works()
        {
            // Arrange
            var oldData = new byte[200];
            var newData = new byte[200];
            for (int i = 0; i < 150; i++)
            {
                newData[i] = 1; // Create 150 differences
            }

            // Act — use high threshold to ensure RLE is used; disable motifs AND arithmetic
            // detection to test pure RLE (XOR ZeroRun/NonZeroRun) behavior. (The 150-byte all-+1
            // change is a uniform per-run arithmetic shift that RunArithmetic 0x0B would otherwise
            // claim — gating it off isolates the baseline RLE opcode breakdown this test asserts.)
            var options = new DeltaZor.DeltaOptions
                { CompressionThreshold = 2.0, EnableMotifDetection = false, EnableArithmeticDetection = false };
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var createStats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(200, applyStats.NewSize);
            // Should have one non-zero run for the 150 changed bytes and one zero run for the 50 unchanged bytes
            Assert.Equal(1, createStats.OpCodeCounts.ZeroRunCount);
            Assert.Equal(1, createStats.OpCodeCounts.NonZeroRunCount);
            Assert.Equal(0, createStats.OpCodeCounts.ExtensionCount);
            Assert.Equal(0, createStats.OpCodeCounts.TruncationCount);
        }

        [Fact(Skip = "Not yet implemented")]
        public void SevenBitEncoding_EdgeCases_HandledCorrectly()
        {
            // TODO: Test encoding of various integer sizes (1, 2, 3, 4, 5 byte encoded values)
            // Verify correct encoding and decoding
        }

        [Fact(Skip = "Not yet implemented")]
        public void SevenBitEncoding_BoundaryValues_WorkCorrectly()
        {
            // TODO: Test boundary values for 7-bit encoding (0x7F, 0x80, 0x3FFF, 0x4000, etc.)
            // Verify correct handling of boundary conditions
        }
    }
}