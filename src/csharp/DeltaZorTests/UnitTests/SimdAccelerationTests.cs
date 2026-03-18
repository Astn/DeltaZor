using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class SimdAccelerationTests
    {
        [Fact]
        public void SIMD_XOR_Equivalence_SmallPayloads()
        {
            // Test that small payloads still use scalar path and produce correct results
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var newData = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32 }; // Force scalar for small data

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void SIMD_XOR_Equivalence_LargePayloads()
        {
            // Test that large payloads use SIMD path and produce correct results
            var random = new Random(42);
            var oldData = new byte[1024];
            var newData = new byte[1024];
            random.NextBytes(oldData);
            random.NextBytes(newData);

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void SIMD_Fallback_PlatformCompatibility()
        {
            // Test graceful fallback when SIMD is not available
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
            var newData = new byte[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40 };

            var options = new DeltaZor.DeltaOptions { UseSIMD = false }; // Force scalar fallback

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void SIMD_BufferManagement_ArrayPool()
        {
            // Test proper ArrayPool usage for large buffers
            var random = new Random(42);
            var oldData = new byte[2048]; // Large enough to trigger ArrayPool
            var newData = new byte[2048];
            random.NextBytes(oldData);
            random.NextBytes(newData);

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32, SimdMaxStackBufferSize = 1024 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void SIMD_Unaligned_Memory()
        {
            // Test SIMD with unaligned memory access
            var random = new Random(42);
            var oldData = new byte[64];
            var newData = new byte[64];
            random.NextBytes(oldData);
            random.NextBytes(newData);

            // Create unaligned spans (offset by 1 byte)
            ReadOnlySpan<byte> oldSpan = oldData.AsSpan(1, 32);
            ReadOnlySpan<byte> newSpan = newData.AsSpan(1, 32);

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 16 };

            // Act
            var delta = DeltaZor.CreateDelta(oldSpan, newSpan, options, out var stats);
            var output = new byte[32];
            var result = DeltaZor.ApplyDelta(oldSpan, delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.True(result.Success);
            Assert.True(output.AsSpan().SequenceEqual(newSpan));
        }

        [Fact]
        public void SIMD_Performance_LargeRun()
        {
            // Performance test for large non-zero runs
            var random = new Random(42);
            var oldData = new byte[1024];
            var newData = new byte[1024];
            random.NextBytes(oldData);
            Buffer.BlockCopy(oldData, 0, newData, 0, oldData.Length);
            // Make 50% of bytes different to ensure RLE is beneficial
            for (int i = 0; i < 512; i++)
                newData[i] = (byte)random.Next(256);

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32, CompressionThreshold = 2.0 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var createStats);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var applyStats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.True(applyStats.UsedRLE); // Should use RLE for this data pattern
        }
    }
}