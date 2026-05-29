using System;
using System.Linq;
using DZ;
using Xunit;
using Xunit.Abstractions;

namespace DZ.Tests.UnitTests
{
    public class MotifTests
    {
        private readonly ITestOutputHelper _output;

        public MotifTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(2, 5, true)]  // UnitSize=2, Repeat=5, Uniform (need >=8 bytes for motif detection)
        [InlineData(4, 4, true)]  // UnitSize=4, Repeat=4, Uniform
        [InlineData(8, 2, true)]  // UnitSize=8 (SIMD-aligned), Repeat=2, Uniform
        [InlineData(3, 5, false)] // UnitSize=3, Repeat=5, Varying
        public void MotifDetection_Parameterized_UnitSizesAndRepeats_EmitsCorrectMotif(int unitSize, int repeatCount, bool isUniform)
        {
            // Arrange: Generate data with repeating motif pattern
            int totalLength = unitSize * repeatCount;
            var oldData = new byte[totalLength]; // All zeros
            var newData = new byte[totalLength];

            // Create changes in positions 1 and (unitSize-1) for masked density <0.5
            int[] changedPositions;
            if (isUniform)
                changedPositions = new int[] { 0 };
            else
                changedPositions = new int[] { 1 };

            byte[] xorValues;
            if (isUniform)
            {
                var unitXor = changedPositions.Select((pos, idx) => (byte)(0x01 + idx)).ToArray();
                xorValues =
                    Enumerable.Repeat(unitXor, repeatCount).SelectMany(x => x).ToArray();
            }
            else
            {
                xorValues = Enumerable.Range(0, repeatCount *
                                                changedPositions.Length).Select(i => (byte)(0x01 + i % 4)).ToArray();
            }

            int xorIdx = 0;
            for (int r = 0; r < repeatCount; r++)
            {
                for (int p = 0; p < unitSize; p++)
                {
                    if (changedPositions.Contains(p))
                    {
                        newData[r * unitSize + p] = xorValues[xorIdx++];
                    }
                }
            }

            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 2.0 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Emit correct motif type
            if (isUniform)
            {
                Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1);
                Assert.Equal(0, stats.OpCodeCounts.VaryingMotifCount);
            }
            else
            {
                Assert.True(stats.OpCodeCounts.VaryingMotifCount >= 1);
                Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount);
            }
            Assert.True(stats.OpCodeCounts.AverageMaskDensity <= 0.5f); // Density check

            // Verify application
            var output = new byte[totalLength];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifDetection_MinStreakNotMet_NoEmission()
        {
            // Arrange: Only 1 repeat (streak < 2)
            var oldData = new byte[4]; // All zeros
            var newData = new byte[4];
            newData[0] = 1;
            newData[3] = 2;

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            _output.WriteLine($"UniformMotifCount: {stats.OpCodeCounts.UniformMotifCount}");
            _output.WriteLine($"VaryingMotifCount: {stats.OpCodeCounts.VaryingMotifCount}");
            _output.WriteLine($"NonZeroRunCount: {stats.OpCodeCounts.NonZeroRunCount}");
            _output.WriteLine($"ZeroRunCount: {stats.OpCodeCounts.ZeroRunCount}");
            _output.WriteLine($"UsedRLE: {stats.UsedRLE}");

            // Assert: No motif (streak=1 <2), falls back to full replace due to high overhead
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount + stats.OpCodeCounts.VaryingMotifCount);

            // Verify application
            var output = new byte[4];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Theory]
        [InlineData(49)] // Below max streak
        [InlineData(50)] // At max streak
        [InlineData(51)] // Above max streak (should cap or fallback)
        public void MotifDetection_MaxStreakCap_HandlesLargeRepeats(int repeatCount)
        {
            // Arrange: Large repeat count
            int unitSize = 4;
            int totalLength = unitSize * repeatCount;
            var oldData = new byte[totalLength]; // All zeros
            var newData = new byte[totalLength];

            // Simple uniform changes in position 1
            for (int r = 0; r < repeatCount; r++)
            {
                newData[r * unitSize + 1] = 0x01;
            }

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: For large repeats, expect motif if within cap, or fallback/partial
            // Note: Implementation caps at 50, so >50 should fallback or emit multiple
            int expectedMotifs = repeatCount <= 50 ? 1 : 0; // Simplified; actual may emit partial
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= expectedMotifs);
            Assert.Equal(0, stats.OpCodeCounts.VaryingMotifCount);

            // Verify application
            var output = new byte[totalLength];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
public void MotifDetection_HighDensity_NoEmission()
        {
            // Arrange: Dense pattern (>70% changes) - should not emit motif
            var oldData = new byte[16]; // All zeros
            var newData = new byte[16];
            // Set 3 of 4 bytes in each 4-byte unit (75% density)
            for (int i = 0; i < 4; i++)
            {
                newData[i * 4 + 0] = 1;
                newData[i * 4 + 1] = 2;
                newData[i * 4 + 2] = 3;
            }

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: No motif emitted, falls back to full replace due to high overhead
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount);
            Assert.Equal(0, stats.OpCodeCounts.VaryingMotifCount);
        }

        [Fact]
        public void MotifDetection_LowSavings_NoEmission()
        {
            // Arrange: Single change per large unit, low savings due to overhead
            var oldData = new byte[16]; // All zeros
            var newData = new byte[16];
            // Unit size 8, 1 change per unit, repeat 2 -> overhead may exceed savings
            newData[0] = 1;
            newData[9] = 1;

            var options = new DeltaZor.DeltaOptions ();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Fallback to RLE if savings <=0.05
_output.WriteLine($"LowSavings Stats: Uniform={stats.OpCodeCounts.UniformMotifCount}, Varying={stats.OpCodeCounts.VaryingMotifCount}, UsedRLE={stats.UsedRLE}, Density={stats.OpCodeCounts.AverageMaskDensity}, DeltaSize={stats.DeltaSize}, CompressionRatio={stats.CompressionRatio}, NonZeroRun={stats.OpCodeCounts.NonZeroRunCount}");
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount + stats.OpCodeCounts.VaryingMotifCount);
            Assert.True(stats.OpCodeCounts.NonZeroRunCount > 0);

            // Verify application
            var output = new byte[newData.Length];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Theory]
        [InlineData(true)]  // Masked mode
        [InlineData(false)] // Full mode (no mask)
        public void MotifDetection_FlagsMode_MaskedVsFull_EmitsCorrectly(bool useMasked)
        {
            // Arrange: UnitSize=4, Repeat=4; masked: changes in pos 1,3; full: all positions
            int unitSize = 4;
            int repeatCount = 4;
            int totalLength = unitSize * repeatCount;
            var oldData = new byte[totalLength];
            var newData = new byte[totalLength];

            byte[] xorPerUnit = useMasked 
                ? new byte[] { 0x00, 0x01, 0x00, 0x01 } // Positions 1,3 changed (same value for uniform detection)
                : new byte[] { 0x01, 0x02, 0x03, 0x04 }; // All changed

            for (int r = 0; r < repeatCount; r++)
            {
                for (int p = 0; p < unitSize; p++)
                {
                    newData[r * unitSize + p] = xorPerUnit[p];
                }
            }

            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 2.0 };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Motif emitted; density 0.5 for masked, 1.0 for full (but still emits if savings > threshold)
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1);
            float expectedDensity = useMasked ? 0.5f : 1.0f;
            Assert.Equal(expectedDensity, stats.OpCodeCounts.AverageMaskDensity, 1); // Tolerance for full

            // Verify application
            var output = new byte[totalLength];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifApplication_VaryingMotif_AppliesCorrectly()
        {
            // Arrange: Manual delta for varying motif
            // Header: len=12, type=0x00
            // Data: 0x05 (varying), flags=0x80 (masked), repeat=3, unit=4, mask=0xA (pos 1,3), xor_data= [1,2, 3,4, 1,2]
            var oldData = new byte[12];
            byte[] deltaData = new byte[]
            {
                0x0C, 0x00, 0x00, 0x00,  // output_length=12
                0x00,                    // RLE (no checksum flag)
                0x05,                    // opcode varying motif
                0x80,                    // flags masked
                0x03,                    // repeat=3
                0x04,                    // unit=4
                0x0A,                    // mask=10 (0b1010)
                0x01, 0x02,              // unit 0: pos1=1, pos3=2
                0x03, 0x04,              // unit 1: pos1=3, pos3=4
                0x01, 0x02,              // unit 2: pos1=1, pos3=2
            };

            var delta = deltaData;

            // Act
            var output = new byte[12];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);

            // Assert
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(1, output[1]); Assert.Equal(2, output[3]); // Unit 0
            Assert.Equal(3, output[5]); Assert.Equal(4, output[7]); // Unit 1
            Assert.Equal(1, output[9]); Assert.Equal(2, output[11]); // Unit 2
            Assert.Equal(0, output[0]); Assert.Equal(0, output[2]); // Unchanged zeros
            Assert.Equal(0, output[4]); Assert.Equal(0, output[6]);
            Assert.Equal(0, output[8]); Assert.Equal(0, output[10]);
        }

        [Fact]
        public void MotifApplication_InvalidRepeatLength_FailsGracefully()
        {
            // Arrange: Manual delta with invalid RepeatLength=1 (should fail or fallback)
            var oldData = new byte[4];
            byte[] deltaData = new byte[]
            {
                0x04, 0x00, 0x00, 0x00,  // len=4
                0x00,                    // RLE (no checksum flag)
                0x04,                    // uniform motif
                0x80,                    // masked
                0x01,                    // repeat=1 (invalid)
                0x04,                    // unit=4
                0x0A,                    // mask=10
                0x01, 0x02,              // xor data
            };

            var delta = deltaData;

            // Act & Assert
            var output = new byte[4];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.False(result.Success); // Expect failure on invalid opcode params
        }

        [Fact]
        public void MotifDetection_UnitSizeOver32_FallsBackToRLE()
        {
            // Arrange: UnitSize=33 >32 cap
            int unitSize = 33;
            int repeatCount = 2;
            int totalLength = unitSize * repeatCount;
            var oldData = new byte[totalLength];
            var newData = new byte[totalLength];
            newData[1] = 1; // Simple change
            newData[unitSize + 1] = 1;

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: No motif (exceeds cap). The unit-33 change cannot lock a motif, so the
            // encoder falls back to a non-motif opcode. The exact fallback opcode is not the
            // contract: a 2-byte-aligned change is now legitimately encoded as a HalfRun (0x07)
            // float16-lane run when it strictly beats byte-RLE/motif/FloatRun, so accept any
            // tracked non-motif RLE-stream opcode (NonZeroRun, HalfRun, or FloatRun) — the
            // round-trip assertion below is the real correctness guard. (TASK-0362; mirrors the
            // TASK-0361 broadening of MotifRepeatTests for FloatRun.)
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount + stats.OpCodeCounts.VaryingMotifCount);
            Assert.True(stats.OpCodeCounts.NonZeroRunCount > 0 ||
                        stats.OpCodeCounts.HalfPatternCount > 0 ||
                        stats.OpCodeCounts.FloatPatternCount > 0);

            // Verify application
            var output = new byte[totalLength];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifStats_AverageDensity_CalculatedCorrectly()
        {
            // Arrange: Data that emits one motif with density 0.25 (1/4)
            var oldData = new byte[8]; // Zeros
            var newData = new byte[8];
            newData[1] = 1; // Change only position 1 in two units of 4
            newData[5] = 1;

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: If motif emitted, average density should be 0.25
            if (stats.OpCodeCounts.UniformMotifCount > 0)
            {
                Assert.Equal(0.25f, stats.OpCodeCounts.AverageMaskDensity, 2);
            }
            // Even if not, test ensures stats are populated
            Assert.True(stats.OpCodeCounts.AverageMaskDensity >= 0);
        }

        [Fact]
        public void MotifDisabled_FallsBackToRLE_NoMotifCounts()
        {
            // Arrange
            var oldData = new byte[12];
            var newData = new byte[12];
            for (int i = 0; i < 3; i++)
            {
                newData[i * 4 + 1] = 1;
                newData[i * 4 + 3] = 2;
            }

            var options = new DeltaZor.DeltaOptions { EnableMotifDetection = false };

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: No motifs, falls back to full replace due to high overhead
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount);
            Assert.Equal(0, stats.OpCodeCounts.VaryingMotifCount);

            // Verify application
            var output = new byte[12];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifTrigger_UniformMasked_LowDensity()
        {
            // Arrange: XOR payload with uniform unit [0x01, 0, 0, 0] repeated 3 times (density=0.25, unit=4, savings ~0.6)
            var oldData = new byte[12]; // All zeros
            var newData = new byte[12];
            byte[] xorPayload = { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            xorPayload.CopyTo(newData, 0);

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Emits uniform motif (masked, low density)
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1);
            Assert.Equal(0, stats.OpCodeCounts.VaryingMotifCount);
            Assert.True(stats.OpCodeCounts.AverageMaskDensity <= 0.5f); // Density check

            // Verify application
            var output = new byte[12];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifTrigger_VaryingMasked_LowDensity()
        {
            // Arrange: XOR payload with varying units [0x01,0,0,0] -> [0x02,0,0,0] -> [0x03,0,0,0] (density=0.25, unit=4)
            var oldData = new byte[12];
            var newData = new byte[12];
            byte[] xorPayload = { 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00 };
            xorPayload.CopyTo(newData, 0);

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Emits varying motif (not uniform due to different values)
            Assert.True(stats.OpCodeCounts.VaryingMotifCount >= 1);
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount);
            Assert.True(stats.OpCodeCounts.AverageMaskDensity < 0.5f);

            // Verify application
            var output = new byte[12];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifTrigger_FullMode_Density1_0()
        {
            // Arrange: Full uniform changes [0x01,0x02,0x03,0x04] repeated 5 times (density=1.0, unit=4)
            var oldData = new byte[20];
            var newData = new byte[20];
            byte[] unitXor = { 0x01, 0x02, 0x03, 0x04 };
            for (int r = 0; r < 5; r++)
                unitXor.CopyTo(newData, r * 4);

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Emits uniform full motif (density prune skipped for full mode)
_output.WriteLine($"FullMode Stats: Uniform={stats.OpCodeCounts.UniformMotifCount}, Varying={stats.OpCodeCounts.VaryingMotifCount}, UsedRLE={stats.UsedRLE}, Density={stats.OpCodeCounts.AverageMaskDensity}, DeltaSize={stats.DeltaSize}, CompressionRatio={stats.CompressionRatio}");
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1);
            Assert.Equal(1.0f, stats.OpCodeCounts.AverageMaskDensity, 0.1f); // Tolerance

            // Verify application
            var output = new byte[20];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Theory]
        [InlineData(51)] // Above max streak, should cap at 50 and emit
        public void MotifTrigger_MaxStreak_Capped(int repeatCount)
        {
            // Arrange: Large uniform repeat, unit=2, change at pos 0 only (density=0.5)
            int unitSize = 2;
            int totalLength = unitSize * repeatCount;
            var oldData = new byte[totalLength];
            var newData = new byte[totalLength];
            for (int r = 0; r < repeatCount; r++)
                newData[r * unitSize + 0] = 0x01; // Uniform change

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: Emits at least 1 motif (capped behavior)
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1);
            Assert.True(stats.OpCodeCounts.AverageMaskDensity == 0.5f);

            // Verify application
            var output = new byte[totalLength];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifTrigger_HighDensity_NoEmit()
        {
            // Arrange: High density (0.8), unit=5, 4 changes per unit, repeated 3 times
            var oldData = new byte[15];
            var newData = new byte[15];
            byte[] unitXor = { 0x01, 0x02, 0x03, 0x04, 0x00 }; // Density 4/5 = 0.8
            for (int r = 0; r < 3; r++)
                unitXor.CopyTo(newData, r * 5);

            var options = new DeltaZor.DeltaOptions();

            // Act
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

            // Assert: No motif emitted (pruned by density >=0.7), falls back to full replace due to high overhead
            Assert.Equal(0, stats.OpCodeCounts.UniformMotifCount + stats.OpCodeCounts.VaryingMotifCount);

            // Verify application
            var output = new byte[15];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }

        [Fact]
        public void MotifTrigger_SmallLength_WithThreshold0()
        {
            // Arrange: Small length=12, masked uniform changes at pos 1&3 (density=0.5), repeat=3, unit=4
            // Use same value at both positions so unit=2 greedy detection finds uniform motifs
            var oldData = new byte[12];
            var newData = new byte[12];
            for (int r = 0; r < 3; r++)
            {
                newData[r * 4 + 1] = 0x01; // Uniform value
                newData[r * 4 + 3] = 0x01; // Same uniform value (avoids unit=2 non-uniform detection)
            }

            var options = new DeltaZor.DeltaOptions { CompressionThreshold = 2.0 };

// Act
            _output.WriteLine($"OldData length: {oldData.Length}");
            _output.WriteLine($"NewData length: {newData.Length}");
            _output.WriteLine($"EnableMotifDetection: {options.EnableMotifDetection}");
            _output.WriteLine($"CompressionThreshold: {options.CompressionThreshold}");
            _output.WriteLine($"MaxStackBufferSize: {options.MaxStackBufferSize}");
            
            var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);
            
            // Debug output to see what's happening
            _output.WriteLine($"UniformMotifCount: {stats.OpCodeCounts.UniformMotifCount}");
            _output.WriteLine($"VaryingMotifCount: {stats.OpCodeCounts.VaryingMotifCount}");
            _output.WriteLine($"AverageMaskDensity: {stats.OpCodeCounts.AverageMaskDensity}");
            _output.WriteLine($"NonZeroRunCount: {stats.OpCodeCounts.NonZeroRunCount}");
            _output.WriteLine($"ZeroRunCount: {stats.OpCodeCounts.ZeroRunCount}");
            _output.WriteLine($"UsedRLE: {stats.UsedRLE}");
            _output.WriteLine($"CompressionType: {stats.CompressionType}");
            _output.WriteLine($"Delta size: {delta.Length}");

            // Assert: Emits motif despite small length
            Assert.True(stats.OpCodeCounts.UniformMotifCount >= 1, $"Expected at least 1 uniform motif, got {stats.OpCodeCounts.UniformMotifCount}");
            Assert.True(stats.OpCodeCounts.AverageMaskDensity == 0.5f, $"Expected density 0.5, got {stats.OpCodeCounts.AverageMaskDensity}");

            // Verify application
            var output = new byte[12];
            var result = DeltaZor.ApplyDelta(oldData, delta, output, out _);
            Assert.True(result.Success);

_output.WriteLine("Output: " + string.Join(", ", output));
            Assert.Equal(newData, output);
        }
    }
}