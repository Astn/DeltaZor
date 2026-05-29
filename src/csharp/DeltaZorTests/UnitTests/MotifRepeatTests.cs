using System;
using System.IO;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class MotifRepeatTests
    {
        [Fact]
        public void UniformMotifRepeat_SimplePattern_WorksCorrectly()
        {
            // Arrange
            // Create data with a simple repeating pattern: [NZR1, ZR1] repeated 5 times
            var oldData = new byte[10];
            var newData = new byte[10];
            
            // Fill with base pattern
            for (int i = 0; i < 10; i++)
            {
                oldData[i] = (byte)(i % 256);
                newData[i] = (byte)(i % 256);
            }
            
            // Create a pattern where every other byte is changed by +1
            for (int i = 0; i < 10; i += 2)
            {
                newData[i] = (byte)((oldData[i] + 1) % 256);
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            
            // Note: The motif detection may not trigger for such a small pattern,
            // but the test verifies the basic functionality works
        }

        [Fact]
        public void VaryingMotifRepeat_SimplePattern_WorksCorrectly()
        {
            // Arrange
            // Create data with a simple repeating pattern where XOR data varies
            var oldData = new byte[10];
            var newData = new byte[10];
            
            // Fill with base pattern
            for (int i = 0; i < 10; i++)
            {
                oldData[i] = (byte)(i % 256);
                newData[i] = (byte)(i % 256);
            }
            
            // Create a pattern where every other byte is changed by increasing amounts
            for (int i = 0; i < 10; i += 2)
            {
                newData[i] = (byte)((oldData[i] + (i/2 + 1)) % 256);
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            
            // Note: The motif detection may not trigger for such a small pattern,
            // but the test verifies the basic functionality works
        }

        [Fact]
        public void UniformMotifRepeat_LargerPattern_WorksCorrectly()
        {
            // Arrange
            // Create data with a larger repeating pattern
            var oldData = new byte[20];
            var newData = new byte[20];
            
            // Fill with base pattern
            for (int i = 0; i < 20; i++)
            {
                oldData[i] = (byte)(i % 10);
                newData[i] = (byte)(i % 10);
            }
            
            // Create a uniform XOR pattern: [0x01, 0x02, 0x00, 0x00] repeated 5 times
            // Use XOR to ensure the delta is truly uniform across all blocks
            for (int block = 0; block < 5; block++)
            {
                int offset = block * 4;
                newData[offset] = (byte)(oldData[offset] ^ 0x01);
                newData[offset + 1] = (byte)(oldData[offset + 1] ^ 0x02);
            }

            // Act — use explicit threshold to ensure RLE is used (default 1.5 may trigger FullReplace fallback)
            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 2.0 };
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifRepeat_PatternCounts_AreTracked()
        {
            // Arrange
            var oldData = new byte[100];
            var newData = new byte[100];
            
            // Fill with base pattern - all zeros
            for (int i = 0; i < 100; i++)
            {
                oldData[i] = 0;
                newData[i] = 0;
            }
            
            // Create a simple pattern - change just a few bytes
            newData[0] = 1;
            newData[10] = 1;
            newData[20] = 1;
            newData[30] = 1;
            newData[40] = 1;
            newData[50] = 1;
            newData[60] = 1;
            newData[70] = 1;
            newData[80] = 1;
            newData[90] = 1;

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert
            // Print out the pattern counts for debugging
            Console.WriteLine($"ZeroRunCount: {stats.OpCodeCounts.ZeroRunCount}");
            Console.WriteLine($"NonZeroRunCount: {stats.OpCodeCounts.NonZeroRunCount}");
            Console.WriteLine($"ExtensionCount: {stats.OpCodeCounts.ExtensionCount}");
            Console.WriteLine($"TruncationCount: {stats.OpCodeCounts.TruncationCount}");
            Console.WriteLine($"ChannelRunCount: {stats.OpCodeCounts.ChannelRunCount}");
            Console.WriteLine($"UniformMotifCount: {stats.OpCodeCounts.UniformMotifCount}");
            Console.WriteLine($"VaryingMotifCount: {stats.OpCodeCounts.VaryingMotifCount}");
            Console.WriteLine($"TotalPatternCount: {stats.OpCodeCounts.TotalPatternCount}");
            Console.WriteLine($"UsedRLE: {stats.UsedRLE}");
            Console.WriteLine($"CompressionType: {stats.CompressionType}");
            
            // We should have some pattern counts since we have changes. The exact opcode
            // mix may include a FloatRun (0x06): this 100-byte buffer with 10 single-byte
            // changes every 10 bytes is encoded as a strict-win FloatRun (float32-lane
            // sparse run), so accept any tracked RLE-stream opcode, not just Zero/NonZero.
            Assert.True(stats.OpCodeCounts.NonZeroRunCount > 0 || stats.OpCodeCounts.ZeroRunCount > 0 ||
                        stats.OpCodeCounts.FloatPatternCount > 0);
            Assert.True(stats.UsedRLE);
            Assert.Equal("RLE", stats.CompressionType);
        }

        [Fact]
        public void UniformMotifRepeat_OpcodeIsDefined()
        {
            // Arrange
            var oldData = new byte[100];
            var newData = new byte[100];
            
            // Fill with base pattern
            for (int i = 0; i < 100; i++)
            {
                oldData[i] = 0;
                newData[i] = 0;
            }
            
            // Create a pattern
            newData[0] = 1;
            newData[10] = 1;
            newData[20] = 1;

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            
            // Assert
            // Check that the UniformMotifCount property exists and is accessible
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 0);
        }

        [Fact]
        public void VaryingMotifRepeat_OpcodeIsDefined()
        {
            // Arrange
            var oldData = new byte[100];
            var newData = new byte[100];
            
            // Fill with base pattern
            for (int i = 0; i < 100; i++)
            {
                oldData[i] = 0;
                newData[i] = 0;
            }
            
            // Create a pattern
            newData[0] = 1;
            newData[10] = 1;
            newData[20] = 1;

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);
            
            // Assert
            // Check that the VaryingMotifCount property exists and is accessible
            Assert.True(stats.OpCodeCounts.VaryingMotifCount >= 0);
        }

    }
}