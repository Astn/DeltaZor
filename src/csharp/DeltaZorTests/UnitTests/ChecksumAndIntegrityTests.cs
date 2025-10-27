using System;
using DZ;
using Xunit;

namespace DZ.Tests.UnitTests
{
    public class ChecksumAndIntegrityTests
    {
        [Fact]
        public void Checksum_Validation_Works()
        {
            // Arrange
            var oldData = new byte[] { 1, 2, 3, 4, 5 };
            var newData = new byte[] { 1, 9, 3, 4, 5 };
            var delta = DeltaZor.CreateDelta(oldData, newData, out var stats);

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

        [Fact(Skip = "Not yet implemented")]
        public void Checksum_Disabled_DoesNotAffectOperation()
        {
            // TODO: Test that disabling checksum works correctly
            // Verify that operations succeed without checksum validation
        }

        [Fact(Skip = "Not yet implemented")]
        public void Checksum_CorruptionDetection_WorksForAllDataTypes()
        {
            // TODO: Test checksum validation with different data types and sizes
            // Verify that corruption is detected in all scenarios
        }
    }
}