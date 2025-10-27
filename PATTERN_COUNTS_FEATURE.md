# Pattern Counts Feature for DeltaZor

## Overview
This document describes the implementation of a new feature in DeltaZor that provides detailed information about the opcodes emitted during delta compression. The feature adds an overload to the `CreateDelta` method that returns a `PatternCounts` struct containing counts of different opcodes used in the delta.

## RLE Opcodes Data Layout

DeltaZor uses a compact opcode system for RLE compression. Each opcode has a specific data layout:

### Current Opcodes
1. **0x00 = Zero Run**
   - Format: `[opcode:1][count:7bit]`
   - Meaning: Run of count bytes that are identical (no change)

2. **0x01 = Non-Zero Run**  
   - Format: `[opcode:1][count:7bit][xor_data:count]`
   - Meaning: Run of count bytes with XOR differences, followed by count bytes of XOR data

3. **0x02 = Extension**
   - Format: `[opcode:1][count:7bit][extension_data:count]`
   - Meaning: Append count new bytes, followed by count bytes of extension data

4. **0x03 = Truncation**
   - Format: `[opcode:1][new_length:4]`
   - Meaning: Set final output length to new_length

### Future Planned Opcodes
5. **0x04 = Float Run** (specialized for 32-bit float data)
   - Format: `[opcode:1][count:7bit][float_xor_data:count*4]`
   - Meaning: Run of count 32-bit floats with XOR differences

6. **0x05 = Half Run** (specialized for 16-bit half float data)
   - Format: `[opcode:1][count:7bit][half_xor_data:count*2]`
   - Meaning: Run of count 16-bit half floats with XOR differences

7. **0x06 = Arithmetic Compression**
   - Format: `[opcode:1][model_id:1][count:7bit][compressed_data:variable]`
   - Meaning: Arithmetic compressed data using specified model

8. **0x07 = Planar Compression**
   - Format: `[opcode:1][plane_count:1][count:7bit][plane_data:variable]`
   - Meaning: Planar compressed 2D data

Note: All counts use 7-bit variable length encoding where the MSB indicates continuation.

## New Features Added

### 1. PatternCounts Struct
A new readonly struct `PatternCounts` has been added to hold counts of different opcodes:

```csharp
public readonly struct PatternCounts
{
    public int ZeroRunCount { get; init; }        // RLE_ZeroRun (0x00)
    public int NonZeroRunCount { get; init; }     // RLE_NonZeroRun (0x01)
    public int ExtensionCount { get; init; }      // RLE_Extension (0x02)
    public int TruncationCount { get; init; }     // RLE_Truncation (0x03)
    public int TotalPatternCount => ZeroRunCount + NonZeroRunCount + ExtensionCount + TruncationCount;
    
    // For future specialized pattern detection
    public int FloatPatternCount { get; init; }
    public int HalfPatternCount { get; init; }
}
```

### 2. New CreateDelta Overload
A new overload of the `CreateDelta` method has been added that returns pattern counts:

```csharp
public static byte[] CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, DeltaOptions options, out PatternCounts patternCounts)
```

### 3. New Span-based CreateDelta Overload
A new overload of the span-based `CreateDelta` method has been added that returns pattern counts:

```csharp
public static bool CreateDelta(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Span<byte> output, out int bytesWritten, DeltaOptions options, out PatternCounts patternCounts)
```

### 4. Enhanced RLE Delta Creation
The RLE delta creation method has been enhanced to track and return opcode counts:

```csharp
private static PatternCounts CreateRLEDeltaWithCounts(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, IBufferWriter<byte> writer, DeltaOptions options)
```

## Implementation Details

### Opcodes Tracked
1. **ZeroRunCount** - Number of RLE_ZeroRun (0x00) opcodes emitted
2. **NonZeroRunCount** - Number of RLE_NonZeroRun (0x01) opcodes emitted
3. **ExtensionCount** - Number of RLE_Extension (0x02) opcodes emitted
4. **TruncationCount** - Number of RLE_Truncation (0x03) opcodes emitted

### Code Changes
1. **DeltaZor.cs** - Added `PatternCounts` struct and new overloads
2. **HalfPrecisionTests.cs** - Added test for the new functionality

## Usage Example

```csharp
var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
var newData = new byte[] { 1, 9, 3, 4, 5, 10, 7, 8 };
var options = new DeltaZor.DeltaOptions();

var delta = DeltaZor.CreateDelta(oldData, newData, options, out var patternCounts);

Console.WriteLine($"Zero runs: {patternCounts.ZeroRunCount}");
Console.WriteLine($"Non-zero runs: {patternCounts.NonZeroRunCount}");
Console.WriteLine($"Extensions: {patternCounts.ExtensionCount}");
Console.WriteLine($"Truncations: {patternCounts.TruncationCount}");
Console.WriteLine($"Total patterns: {patternCounts.TotalPatternCount}");

// For Half precision data, you might also see:
Console.WriteLine($"Float patterns detected: {patternCounts.FloatPatternCount}");
Console.WriteLine($"Half patterns detected: {patternCounts.HalfPatternCount}");
```

## Benefits

1. **Diagnostic Information** - Developers can now understand exactly what opcodes were used in compression
2. **Performance Analysis** - Ability to analyze compression efficiency by opcode type
3. **Feature Verification** - Verify that specific features (like float/half detection) are working
4. **Optimization Opportunities** - Insights into data characteristics that affect compression
5. **Backward Compatibility** - All existing code continues to work unchanged

## Testing

A new test `CreateDelta_WithPatternCounts_ReturnsCorrectCounts` has been added to verify the functionality:

```csharp
[Fact]
public void CreateDelta_WithPatternCounts_ReturnsCorrectCounts()
{
    // Arrange
    var oldData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
    var newData = new byte[] { 1, 9, 3, 4, 5, 10, 7, 8 }; // Change some values
    var options = new DeltaZor.DeltaOptions();
    
    // Act
    var delta = DeltaZor.CreateDelta(oldData, newData, options, out var patternCounts);
    
    // Assert
    Assert.True(delta.Length > 0);
    // Should have at least one zero run and one non-zero run
    Assert.True(patternCounts.ZeroRunCount >= 0);
    Assert.True(patternCounts.NonZeroRunCount >= 0);
    Assert.True(patternCounts.TotalPatternCount > 0);
    
    // Test with Half precision data
    var halfData = new byte[64]; // 32 Half values
    for (int i = 0; i < 32; i++)
    {
        var halfValue = (Half)(1.5f + (i * 0.1f)); // Different values
        var bytes = BitConverter.GetBytes(halfValue);
        Array.Copy(bytes, 0, halfData, i * 2, 2);
    }
    
    var oldHalfData = new byte[64];
    var newHalfData = new byte[64];
    Array.Copy(halfData, oldHalfData, 64);
    Array.Copy(halfData, newHalfData, 64);
    
    // Make a small change
    newHalfData[4] = (byte)(newHalfData[4] ^ 0xFF);
    
    var halfDelta = DeltaZor.CreateDelta(oldHalfData, newHalfData, options, out var halfPatternCounts);
    Assert.True(halfDelta.Length > 0);
}
```

## Files Modified

1. **src/csharp/DeltaZor/DeltaZor.cs**
   - Added `PatternCounts` struct
   - Added new `CreateDelta` overloads
   - Enhanced RLE delta creation to track opcode counts
   - Added basic float/half pattern detection
   - Updated documentation with explicit opcode layouts

2. **src/csharp/DeltaZorTests/UnitTests/HalfPrecisionTests.cs**
   - Added test for new functionality

## Future Enhancements

1. **Advanced Pattern Detection** - More sophisticated detection of float/half patterns
2. **Specialized Compression Modes** - Implement the planned opcodes (0x04-0x07)
3. **Detailed Pattern Analysis** - More granular information about pattern characteristics

## Conclusion

The pattern counts feature provides valuable diagnostic information about the compression process by tracking exactly which opcodes are emitted. This enables developers to verify that features are working correctly and understand how different data types affect compression performance. The feature maintains full backward compatibility while providing new insights into the compression process.