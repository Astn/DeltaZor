using System;
using System.Runtime.InteropServices;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class ArithmeticCompressionTests
    {
        [Fact]
        public void GlobalArithmeticShift_DetectsAndAppliesUniformIntegerShift()
        {
            // Arrange: 250K int32 values, all += 5 (uniform global arithmetic shift). XOR-delta of
            // x vs x+5 is carry-dependent noise the XOR opcodes encode poorly; the arithmetic
            // difference is a constant +5 per int32 lane, so 0x09 captures the whole 1M-byte region
            // in a handful of bytes.
            var oldData = new byte[1000000]; // 1M bytes = 250K int32 values
            var newData = new byte[1000000];
            for (int i = 0; i < 250000; i++)
            {
                BitConverter.GetBytes(i * 10).CopyTo(oldData, i * 4);
                BitConverter.GetBytes(i * 10 + 5).CopyTo(newData, i * 4); // +5 shift
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert: a single GlobalArithmetic (0x09) opcode, tiny delta, exact round-trip.
            Assert.Equal(1, stats.OpCodeCounts.ArithmeticCount);
            Assert.True(delta.Length < 100, $"expected tiny arithmetic delta, got {delta.Length}");

            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void PlanarArithmetic_DetectsAndAppliesPerChannelShifts()
        {
            // Arrange: 4000 RGBA pixels where each channel shifts by its own constant (R+10, G-6
            // via byte wraparound, B+1, A+0). The per-plane arithmetic difference is uniform per
            // channel, so PlanarArithmetic (0x0A, planeCount=4) captures the whole region.
            const int px = 4000;
            var oldData = new byte[px * 4];
            var newData = new byte[px * 4];
            var rng = new Random(7);
            for (int i = 0; i < px; i++)
            {
                byte r = (byte)rng.Next(256), g = (byte)rng.Next(256),
                     b = (byte)rng.Next(256), a = (byte)rng.Next(256);
                oldData[i * 4] = r; oldData[i * 4 + 1] = g; oldData[i * 4 + 2] = b; oldData[i * 4 + 3] = a;
                newData[i * 4] = (byte)(r + 10);
                newData[i * 4 + 1] = (byte)(g + 250); // -6 wraparound
                newData[i * 4 + 2] = (byte)(b + 1);
                newData[i * 4 + 3] = a; // +0
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert: a single PlanarArithmetic (0x0A) opcode, tiny delta, exact round-trip.
            Assert.Equal(1, stats.OpCodeCounts.PlanarCount);
            Assert.True(delta.Length < 100, $"expected tiny planar delta, got {delta.Length}");

            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void ArithmeticModes_YieldOnNonArithmeticData()
        {
            // Random unstructured changes have no uniform per-lane/per-plane step, so neither 0x09
            // nor 0x0A may fire — the region must fall through to the XOR/RLE pipeline unchanged.
            var oldData = new byte[2048];
            var newData = new byte[2048];
            var rng = new Random(3);
            rng.NextBytes(oldData);
            rng.NextBytes(newData);

            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            Assert.Equal(0, stats.OpCodeCounts.ArithmeticCount);
            Assert.Equal(0, stats.OpCodeCounts.PlanarCount);

            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact(Skip = "Not yet implemented")]
        public void PerRunArithmetic_DetectsAndAppliesLocalUniformChanges()
        {
            // TODO: Implement per-run arithmetic detection
            // Test with runs of bytes that all have the same delta
            // Should encode as arithmetic runs rather than XOR runs when beneficial
        }

        [Fact(Skip = "Arithmetic compression not yet implemented")]
        public void RunArithmeticOpcode_CorrectlyEncodesAndDecodes()
        {
            // Arrange
            // Create test data with runs of bytes that all have the same delta
            var oldData = new byte[1000];
            var newData = new byte[1000];
            
            // Fill with base pattern
            for (int i = 0; i < 1000; i++)
            {
                oldData[i] = (byte)(i % 256);
                newData[i] = (byte)(i % 256);
            }
            
            // Create a run of uniform changes (+10 to bytes 100-199)
            for (int i = 100; i < 200; i++)
            {
                newData[i] = (byte)((oldData[i] + 10) % 256);
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert
            // When arithmetic compression is implemented, this should use RunArithmetic opcode (0x04)
            // For now, we're just documenting the expected behavior
            Assert.NotNull(delta);
            // TODO: When implemented, verify that the delta contains the RunArithmetic opcode
            // and that it correctly encodes the arithmetic operation
        }

        [Fact(Skip = "Not yet implemented")]
        public void ClampAwareDetection_HandlesOverflowCorrectly()
        {
            // TODO: Implement clamp-aware detection
            // Test with byte arrays where arithmetic would cause overflow
            // Should clamp values correctly (255+10=255)
        }

        [Fact(Skip = "Not yet implemented")]
        public void AutoModeSelection_ChoosesBestCompressionMode()
        {
            // TODO: Implement auto-mode selection
            // Test that the system correctly chooses between RLE, arithmetic, planar, etc.
            // Based on data characteristics
        }

        [Fact]
        public void FloatPatternDetection_DetectsFloatLikeData()
        {
            // Arrange
            // Create data that looks like float values in the range [-2.0, 2.0]
            var floatData = new byte[1024]; // 256 floats
            
            // Fill with float values that would have exponents in the expected range
            for (int i = 0; i < 256; i++)
            {
                float value = (i % 100) / 25.0f - 2.0f; // Values between -2.0 and 2.0
                var bytes = BitConverter.GetBytes(value);
                Array.Copy(bytes, 0, floatData, i * 4, 4);
            }

            // Act & Assert
            // This test verifies our experimental float detection doesn't crash
            // and provides the expected interface (actual implementation may change)
            
            // Note: We can't directly test the private FloatPatternDetector,
            // but we can verify that our enhanced ShouldUseRLE method still works
            var oldData = new byte[1024];
            var newData = new byte[1024];
            Array.Copy(floatData, oldData, 1024);
            Array.Copy(floatData, newData, 1024);
            
            // This should not throw an exception
            var options = new DeltaZor.DeltaOptions();
            var result = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            
            // The test passes if we get here without exceptions and compression occurred for identical float data
            Assert.True(result.Length < newData.Length); 
        }
    }
}