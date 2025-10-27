using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class HalfPrecisionTests
    {
        [Fact]
        public void HalfPrecision_HeightMap_CompressionWorks()
        {
            // Arrange
            int width = 64, height = 64;
            var heightMap = new Half[width * height];
            var random = new Random(42);
            
            // Create realistic height map using Half precision
            for (int i = 0; i < heightMap.Length; i++)
            {
                float value = (float)(random.NextDouble() * 4.0 - 2.0);
                
                // Add terrain-like pattern
                int x = i % width;
                int y = i / width;
                float distance = (float)Math.Sqrt(Math.Pow(x - width/2, 2) + Math.Pow(y - height/2, 2));
                value += (float)(Math.Sin(distance * 0.1) * 0.5);
                
                // Clamp to our target range [-2.0, 2.0]
                value = Math.Max(-2.0f, Math.Min(2.0f, value));
                
                heightMap[i] = (Half)value;
            }
            
            // Convert to byte array for DeltaZor
            var data = ConvertHalfArrayToBytes(heightMap);
            
            var oldData = new byte[data.Length];
            var newData = new byte[data.Length];
            Array.Copy(data, newData, data.Length);
            
            var options = new DeltaZor.DeltaOptions();
            
            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            
            // Assert
            Assert.True(delta.Length > 0);
            // Verify no exceptions during float pattern analysis
        }
        
        [Fact]
        public void HalfPrecision_DeltaCompression_Works()
        {
            // Arrange
            int width = 32, height = 32;
            var oldMap = new Half[width * height];
            var newMap = new Half[width * height];
            var random = new Random(42);
            
            // Create base terrain
            for (int i = 0; i < oldMap.Length; i++)
            {
                float value = (float)(random.NextDouble() * 4.0 - 2.0);
                int x = i % width;
                int y = i / width;
                float distance = (float)Math.Sqrt(Math.Pow(x - width/2, 2) + Math.Pow(y - height/2, 2));
                value += (float)(Math.Sin(distance * 0.05) * 0.3);
                value = Math.Max(-2.0f, Math.Min(2.0f, value));
                oldMap[i] = (Half)value;
            }
            
            // Create modified terrain with specific changes
            Array.Copy(oldMap, newMap, oldMap.Length);
            
            // Add elevation change in center
            for (int y = 14; y < 18; y++)
            {
                for (int x = 14; x < 18; x++)
                {
                    int index = y * width + x;
                    newMap[index] = (Half)((float)newMap[index] + 0.15f);
                }
            }
            
            // Convert to bytes
            var oldData = ConvertHalfArrayToBytes(oldMap);
            var newData = ConvertHalfArrayToBytes(newMap);
            
            // Act
            var options = new DeltaZor.DeltaOptions();
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            
            // Test delta application
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out var stats);
            
            // Assert
            Assert.True(result.Success);
            Assert.True(delta.Length > 0);
            
            // Verify integrity
            bool matches = true;
            for (int i = 0; i < newData.Length; i++)
            {
                if (output[i] != newData[i])
                {
                    matches = false;
                    break;
                }
            }
            Assert.True(matches);
        }
        
        [Fact]
        public void MixedPrecision_WithFloatDetection_Works()
        {
            // Arrange
            // Create data that contains various patterns that might trigger float detection
            var data = new byte[1024];
            var random = new Random(43);
            
            // First 256 bytes: Half precision values (should trigger float detection)
            for (int i = 0; i < 128; i++)
            {
                float value = (float)(random.NextDouble() * 4.0 - 2.0);
                var halfValue = (Half)value;
                var bytes = BitConverter.GetBytes(halfValue);
                Array.Copy(bytes, 0, data, i * 2, 2);
            }
            
            // Next 256 bytes: Sequential patterns (should not trigger float detection)
            for (int i = 256; i < 512; i++)
            {
                data[i] = (byte)(i % 256);
            }
            
            // Next 256 bytes: Random patterns (should not trigger float detection)
            var randomData = new byte[256];
            random.NextBytes(randomData);
            Array.Copy(randomData, 0, data, 512, 256);
            
            // Last 256 bytes: More Half precision values
            for (int i = 384; i < 512; i++)
            {
                float value = (float)(random.NextDouble() * 4.0 - 2.0);
                var halfValue = (Half)value;
                var bytes = BitConverter.GetBytes(halfValue);
                Array.Copy(bytes, 0, data, 768 + (i - 384) * 2, 2);
            }
            
            var oldData = new byte[data.Length];
            var newData = new byte[data.Length];
            Array.Copy(data, newData, data.Length);
            
            // Make some small changes to create actual delta
            newData[100] = (byte)(newData[100] ^ 0xFF);
            newData[300] = (byte)(newData[300] + 1);
            newData[700] = (byte)(newData[700] - 1);
            
            // Act
            var options = new DeltaZor.DeltaOptions();
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            
            // Assert
            Assert.True(delta.Length > 0);
            // No exceptions during mixed pattern analysis
        }
        
        [Fact]
        public void FloatPatternDetection_DetectsHalfPrecisionData()
        {
            // Arrange
            var halfData = new byte[512];
            var random = new Random(44);
            
            // Fill with Half precision values
            for (int i = 0; i < 256; i++)
            {
                float value = (float)(random.NextDouble() * 4.0 - 2.0);
                var halfValue = (Half)value;
                var bytes = BitConverter.GetBytes(halfValue);
                Array.Copy(bytes, 0, halfData, i * 2, 2);
            }
            
            // Act & Assert
            // This test verifies that the FloatPatternDetector doesn't throw exceptions
            // and can handle Half precision data without issues
            var oldData = new byte[halfData.Length];
            var newData = new byte[halfData.Length];
            Array.Copy(halfData, newData, halfData.Length);
            
            var options = new DeltaZor.DeltaOptions();
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            
            Assert.True(delta.Length > 0);
        }
        
        [Fact]
        public void HalfPrecision_ExceptionalCompressionRatio()
        {
            // Arrange
            var halfData = new byte[1024];
            var random = new Random(45);
            
            // Create data with consistent Half values (should compress very well)
            for (int i = 0; i < 512; i++)
            {
                var halfValue = (Half)1.5f; // Same value repeatedly
                var bytes = BitConverter.GetBytes(halfValue);
                Array.Copy(bytes, 0, halfData, i * 2, 2);
            }
            
            var oldData = new byte[halfData.Length];
            var newData = new byte[halfData.Length];
            Array.Copy(halfData, newData, halfData.Length);
            
            // Make a small change
            newData[100] = (byte)(newData[100] ^ 0xFF);
            
            // Act
            var options = new DeltaZor.DeltaOptions();
            var delta = DeltaZor.CreateDelta(oldData, newData, options);
            
            // Assert
            // Even with identical data, should get good compression
            double ratio = (double)delta.Length / newData.Length;
            Assert.True(ratio < 1.0); // Should always compress better than 1:1
        }
        
        private static byte[] ConvertHalfArrayToBytes(Half[] halfArray)
        {
            var bytes = new byte[halfArray.Length * 2];
            for (int i = 0; i < halfArray.Length; i++)
            {
                var halfBytes = BitConverter.GetBytes(halfArray[i]);
                Array.Copy(halfBytes, 0, bytes, i * 2, 2);
            }
            return bytes;
        }
    }
}