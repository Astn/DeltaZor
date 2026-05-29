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

        [Fact]
        public void PerRunArithmetic_DetectsAndAppliesLocalUniformChanges()
        {
            // Arrange: a 2000-byte buffer that is a single arithmetic SEGMENT (bytes 500-1499 all
            // shifted by a constant +7, byte wraparound) surrounded by unchanged regions. Whole-
            // region 0x09/0x0A cannot fire (uniformity breaks at the segment edges); per-run
            // RunArithmetic (0x0B) captures the segment in 4 bytes instead of a 1000-byte XOR run.
            var oldData = new byte[2000];
            var newData = new byte[2000];
            var rng = new Random(11);
            rng.NextBytes(oldData);
            oldData.CopyTo(newData, 0);
            for (int i = 500; i < 1500; i++)
                newData[i] = (byte)(oldData[i] + 7); // local +7 wraparound segment

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert: a single RunArithmetic (0x0B) opcode, no whole-region 0x09/0x0A, exact round-trip.
            Assert.Equal(1, stats.OpCodeCounts.RunArithmeticCount);
            Assert.Equal(0, stats.OpCodeCounts.ArithmeticCount);
            Assert.Equal(0, stats.OpCodeCounts.PlanarCount);
            Assert.True(delta.Length < 100, $"expected tiny per-run delta, got {delta.Length}");

            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void RunArithmeticOpcode_CorrectlyEncodesAndDecodes()
        {
            // Arrange: base pattern with a local run of uniform +10 changes over [100,200) — the
            // classic per-run arithmetic case. The XOR of (i%256) vs (i%256)+10 is carry noise the
            // XOR run encodes poorly; RunArithmetic (0x0B) captures the run in 4 bytes.
            var oldData = new byte[1000];
            var newData = new byte[1000];
            for (int i = 0; i < 1000; i++)
            {
                oldData[i] = (byte)(i % 256);
                newData[i] = (byte)(i % 256);
            }
            for (int i = 100; i < 200; i++)
                newData[i] = (byte)((oldData[i] + 10) % 256);

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert: the RunArithmetic (0x0B) opcode fires and the delta round-trips exactly.
            Assert.Equal(1, stats.OpCodeCounts.RunArithmeticCount);
            Assert.NotNull(delta);

            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
        }

        [Fact]
        public void ClampAwareDetection_HandlesOverflowCorrectly()
        {
            // Arrange: a local run where the intended signed step (+10) saturates at the byte ceiling
            // for high old values (250+10 -> 255, 255+10 -> 255) and at the floor for a separate run
            // (3 + (-10) -> 0). The encoder must detect clamp-mode RunArithmetic (0x0B flags bit0=1)
            // and round-trip EXACTLY — clamp is lossless because decode replays clamp(old+step) on
            // the still-untouched old byte. A non-saturated byte in each run anchors the exact step.
            var oldData = new byte[600];
            var newData = new byte[600];
            // Background: identical.
            // Ceiling-clamp run over [100,200): old ramps through 240..255 then stays 255; +10.
            for (int i = 100; i < 200; i++)
            {
                byte ov = (byte)Math.Min(255, 240 + (i - 100)); // 240,241,...255,255,...
                oldData[i] = ov;
                int r = ov + 10; if (r > 255) r = 255;
                newData[i] = (byte)r;
            }
            // Floor-clamp run over [300,400): old ramps 15,14,...0 then stays 0; -10.
            for (int i = 300; i < 400; i++)
            {
                byte ov = (byte)Math.Max(0, 15 - (i - 300));
                oldData[i] = ov;
                int r = ov - 10; if (r < 0) r = 0;
                newData[i] = (byte)r;
            }

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

            // Assert: clamp-aware RunArithmetic fires (>=1 run) and round-trips exactly at both
            // saturation boundaries.
            Assert.True(stats.OpCodeCounts.RunArithmeticCount >= 1,
                $"expected clamp RunArithmetic, got {stats.OpCodeCounts.RunArithmeticCount}");
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);
            Assert.Equal(newData, output);
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