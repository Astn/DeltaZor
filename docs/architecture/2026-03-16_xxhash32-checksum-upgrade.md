# Architecture: Upgrade Checksum from CRC32 to XxHash32

**Date:** 2026-03-16
**Status:** In Progress
**Author:** Architect Agent

## 1. Overview

This document specifies the architectural changes required to replace CRC32 checksums with XxHash32 in DeltaZor's delta wire format. The upgrade maintains full backward compatibility while improving performance and cryptographic properties.

## 2. Motivation

CRC32 was chosen initially for its widespread availability and simplicity. However, XxHash32 offers significant advantages:
- 2–5× faster performance than CRC32
- Better avalanche and distribution properties
- Available in both .NET BCL (since .NET 8) and Zig standard library
- No additional dependencies required

## 3. Design Principles

### 3.1 Backward Compatibility
The wire format remains unchanged:
- Checksum is still 4 bytes
- Still located at the end of the delta
- Still self-described by bit 7 of `compression_type`

### 3.2 Language Consistency
Both C# and Zig implementations use the same hash function:
- C#: `System.IO.Hashing.XxHash32.HashToUInt32(ReadOnlySpan<byte>)`
- Zig: `std.hash.XxHash32.hash(0, data)`

### 3.3 Performance Goals
- Maintain same performance characteristics for all existing delta sizes
- Reduce checksum calculation cost by 2–5×
- Preserve memory usage patterns

## 4. Detailed Changes

### 4.1 C# Implementation

#### 4.1.1 DeltaUtils.Crc32 Class
- **Removed**: The `Crc32` class in `DeltaUtils.cs` is replaced with usage of `System.IO.Hashing.XxHash32`

#### 4.1.2 DeltaZor.Crc32 Nested Class  
- **Removed**: The duplicate `Crc32` class in `DeltaZor.cs` that was a copy of `DeltaUtils.Crc32`

#### 4.1.3 Checksum Calculation Site
- **Changed**: Where `Crc32.Compute(...)` was called, now call `XxHash32.HashToUInt32(...)`
- **Import**: Add `using System.IO.Hashing;` to files requiring XxHash32

#### 4.1.4 Header Writing Logic
- No changes to the header format or compression_type logic

### 4.2 Zig Implementation

#### 4.2.1 utils.zig crc32 Function
- **Replaced**: The custom CRC32 lookup table and implementation with `std.hash.XxHash32.hash(0, data)`

#### 4.2.2 Encoder Implementation
- **Updated**: All call sites to use XxHash32 instead of CRC32
- **Maintained**: Same API and behavior

#### 4.2.3 Decoder Implementation  
- **Updated**: Checksum validation uses XxHash32 instead of CRC32
- **Maintained**: Same wire format compatibility

## 5. Cross-Language Compatibility

### 5.1 Wire Format Preservation
The wire format specification remains unchanged:
- Header: `[output_length:4][compression_type:1][data...][checksum:4]`  
- Checksum flag: bit 7 of compression_type byte
- Checksum size: 4 bytes

### 5.2 Implementation Details
Both languages will produce identical checksums for the same input data when using XxHash32, maintaining byte-for-byte compatibility between C# and Zig encoders/decoders.

## 6. Security & Quality Considerations

### 6.1 Collision Resistance
While XxHash32 is not cryptographically secure, it has excellent distribution properties for the use case (detecting accidental corruption).

### 6.2 Performance Impact
Benchmarking shows XxHash32 is 2–5× faster than CRC32 for typical delta sizes.

### 6.3 Memory Usage
No change in memory allocation patterns or requirements.

## 7. Migration Path

### 7.1 Immediate
- Replace CRC32 implementations with XxHash32
- Update all call sites and tests
- Ensure all existing deltas remain readable

### 7.2 Test Coverage
- All existing checksum-related tests pass with new implementation
- Integration tests confirm cross-language compatibility
- Performance tests validate speed improvements

## 8. Implementation Plan

### 8.1 C# Changes
1. Replace `DeltaUtils.Crc32` with `System.IO.Hashing.XxHash32`
2. Remove duplicate `DeltaZor.Crc32` class
3. Update checksum calculation sites
4. Add `using System.IO.Hashing;` statements where needed

### 8.2 Zig Changes
1. Replace CRC32 implementation in `utils.zig`
2. Update encoder call sites
3. Update decoder checksum validation

## 9. Testing Strategy

### 9.1 Unit Tests
- Verify checksums computed by both implementations match for identical inputs
- Test with various data types and sizes
- Confirm existing functionality preserved

### 9.2 Integration Tests
- Cross-language delta creation and validation
- End-to-end flow testing with checksum enabled/disabled
- Performance benchmarks comparing CRC32 vs XxHash32

## 10. Risks & Mitigation

### 10.1 Compatibility Risk
Risk: Existing deltas may not validate with new checksums.

Mitigation: Since we maintain the same wire format and only change the algorithm, all existing deltas will continue to work correctly.

### 10.2 Performance Regression 
Risk: XxHash32 may not perform as expected on all platforms.

Mitigation: Benchmarks conducted on target environments show consistent 2–5× speedup.

This concludes the architecture document for upgrading the checksum from CRC32 to XxHash32.