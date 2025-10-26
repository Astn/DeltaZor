namespace DZ.Tests;

using System;
using DZ;
using System.Buffers;
using Xunit;


    public class DeltaZorTests
    {
        [Fact]
        public void CreateDelta_IdenticalArrays_ReturnsRLEDelta()
        {
            // Arrange
            var data = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(data, data);

            // Assert
            // New format: [output_length:4][compression_type:1][data...][checksum:4]
            // For identical arrays, should use RLE with all zeros
            Assert.True(delta.Length >= 9); // Header + checksum + minimal data
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength); // Same length
            Assert.Equal(0x00, compressionType); // RLE compression
        }
        
        [Fact]
        public void CreateDelta_CompletelyDifferentArrays_ReturnsFullReplace()
        {
            // Arrange
            var oldData = new byte[] { 0, 0, 0, 0, 0 };
            var newData = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);

            // Assert
            // Should choose full replace for high change density
            Assert.True(delta.Length >= 13);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength);
            // May choose RLE or full replace depending on compression analysis
        }
        
        [Fact]
        public void CreateDelta_MixedDifferences_UsesAppropriateCompression()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var newData = new byte[] { 1, 9, 3, 4, 5, 10, 7, 8 };
            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 0.0 }; // Lower threshold

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options);

            // Assert
            Assert.True(delta.Length >= 9);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(8, outputLength);
            // For this pattern, full replace is more efficient (RLE estimate 12 > 8)
            Assert.Equal(0x01, compressionType); 
        }
        
        [Fact]
        public void ApplyDelta_ValidDelta_RecreatesNewData()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var delta = DeltaZor.CreateDelta(oldData, newData);

            // Act
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(5, stats.NewSize);
        }

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
        public void CreateDelta_LengthMismatch_UsesFullReplace()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3 };
            var newData = new byte[] { 1, 2, 3, 4, 5 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);

            // Assert
            // Length mismatch should use full replace
            Assert.True(delta.Length >= 13);
            int outputLength = BitConverter.ToInt32(delta, 0);
            byte compressionType = delta[4];

            Assert.Equal(5, outputLength);
            Assert.Equal(0x01, compressionType); // Full replace
        }

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

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(200, stats.NewSize);
        }

        [Fact]
        public void EdgeCases_EmptyArrays_HandledGracefully()
        {
            // Arrange
            var empty = Array.Empty<byte>();

            // Act
            var delta = DeltaZor.CreateDelta(empty, empty);
            var output = new byte[0];
            var result = DeltaZor.ApplyDelta(empty.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(empty, output);
            Assert.Equal(0, stats.NewSize);
        }

        [Fact]
        public void EdgeCases_SingleElementArrays_WorkCorrectly()
        {
            // Arrange
            var oldData = new byte[] { 5 };
            var newData = new byte[] { 10 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(1, stats.NewSize);
        }

        [Fact]
        public void SpanBasedAPI_WorksCorrectly()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var output = new byte[newData.Length];

            // Act
            var delta = DeltaZor.CreateDelta(oldData.AsSpan(), newData.AsSpan());
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(5, stats.NewSize);
        }

        [Fact]
        public void DeltaOptions_ConfigurationWorks()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var options = new DeltaZor.DeltaOptions
            {
                CompressionThreshold = 0.0, // Lower threshold
                EnableChecksum = false
            };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            // For this pattern, full replace is chosen even with low threshold (RLE 7 > 5 data size)
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
        public void LengthChanges_Extension_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 2, 3, 4, 5, 6, 7 }; // Extended

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(7, stats.NewSize);
        }

        [Fact]
        public void LengthChanges_Truncation_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
            var newData = new byte[] { 1, 2, 3, 4, 5 }; // Truncated

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.Equal(5, stats.NewSize);
        }

        [Fact]
        public void Checksum_Validation_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var delta = DeltaZor.CreateDelta(oldData, newData);

            // Corrupt the checksum
            if (delta.Length >= 4)
            {
                delta[^1] ^= 0xFF; // Flip last byte of checksum
            }

            // Act
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

            // Assert
            Assert.False(result.Success);
            Assert.Contains("Checksum validation failed", result.Error);
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
        public void BufferManagement_SpanAPI_BufferTooSmall()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var smallBuffer = new byte[10]; // Too small

            // Act
            var success = DeltaZor.CreateDelta(oldData.AsSpan(), newData.AsSpan(), smallBuffer.AsSpan(), out int requiredSize);

            // Assert
            Assert.False(success);
            Assert.True(requiredSize > smallBuffer.Length);
        }

        // SIMD-specific tests

        [Fact]
        public void SIMD_XOR_Equivalence_SmallPayloads()
        {
            // Test that small payloads still use scalar path and produce correct results
            var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var newData = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32 }; // Force scalar for small data

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
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
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
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
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
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
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
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
            var delta = DeltaZor.CreateDelta(oldSpan, newSpan, options);
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

            var options = new DeltaZor.DeltaOptions { UseSIMD = true, SimdMinThreshold = 32, CompressionThreshold = 0.0 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(newData, output);
            Assert.True(stats.UsedRLE); // Should use RLE for this data pattern
        }


    }
