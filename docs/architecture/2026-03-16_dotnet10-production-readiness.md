# Architecture: DeltaZor .NET 10 Production Readiness

**Date:** 2026-03-16
**Status:** Draft
**Author:** Architect Agent
**Supersedes:** Sections 5.7, 5.8 of `docs/architecture/2026-03-15_production-readiness.md` (dead code removal, DefaultOptions fix)

---

## 1. Overview

This document specifies the remaining cleanup needed to make DeltaZor production-ready on .NET 10. It covers:

1. .NET 9 → .NET 10 target framework upgrade
2. Dead code removal from `DeltaZor.cs` (duplicate private methods/constants shadowing `DeltaUtils`)
3. `DefaultOptions.CompressionThreshold` fix (2.0 → 0.95)
4. Test fixes for `ChecksumAndIntegrityTests` and `MotifTests`
5. Zig `tests.zig` SHA256 hex zero-padding fix
6. `build.zig` net9.0 → net10.0 reference update

The canonical wire format (§2 of `docs/architecture/2026-03-15_production-readiness.md`) is **unchanged**. The XxHash32 checksum upgrade (`docs/architecture/2026-03-16_xxhash32-checksum-upgrade.md`) is already complete.

---

## 2. Module Boundaries After Cleanup

### 2.1 DeltaZor.cs — Public API Only

After cleanup, `DeltaZor.cs` MUST contain ONLY:

| Element | Type | Purpose |
|---------|------|---------|
| `DeltaOptions` | public class | Configuration record |
| `DeltaStats` | public readonly struct | Compression statistics |
| `DeltaResult<T>` | public readonly struct | Result wrapper |
| `OpCodeCounts` | public record struct | Opcode emission counters |
| `CreateDelta(...)` | public static methods (4 overloads) | Encode entry points |
| `ApplyDelta(...)` | public static method | Decode entry point |
| `AnalyzeDelta(...)` | public static method | Analysis without encoding |

`DeltaZor.cs` MUST NOT contain:
- Any `private` methods (all implementation lives in `DeltaUtils`, `DeltaEncoder`, `DeltaDecoder`)
- Any `private const` or `private static readonly` fields (all constants live in `DeltaUtils`)
- Any `#region` blocks for SIMD or private implementation
- Any `unsafe` keyword (the removed SIMD methods were the only unsafe code)

The file already has `using static DeltaUtils;`, `using static DeltaEncoder;`, and `using static DeltaDecoder;`, so all references to removed private members resolve automatically to `DeltaUtils.*`.

### 2.2 DeltaUtils.cs — Shared Constants & Utilities (No Changes Except DefaultOptions)

`DeltaUtils.cs` retains all existing `internal` constants, SIMD helpers, varint helpers, `SpanReader`, `MotifCandidate`, `ChannelPattern`, `XxHash32Wrapper`, and `DefaultOptions`. The only change is fixing `DefaultOptions.CompressionThreshold` from `2.0` to `0.95`.

### 2.3 Encoder.cs / Decoder.cs — No Changes

These files are not affected by this cleanup.

---

## 3. Dead Code Removal Specification

### 3.1 Lines to Remove from DeltaZor.cs

The following sections and their contents MUST be deleted entirely:

#### Header and Compression Constants (lines 44–51)

```csharp
// REMOVE — duplicates DeltaUtils.HeaderSize, ChecksumSize, MinDeltaSize,
//          CompressionType_RLE, CompressionType_FullReplace
private const int HeaderSize = sizeof(int) + sizeof(byte);
private const int ChecksumSize = sizeof(uint);
private const int MinDeltaSize = HeaderSize + ChecksumSize;
private const byte CompressionType_RLE = 0x00;
private const byte CompressionType_FullReplace = 0x01;
```

#### Opcode Comment Block and Constants (lines 53–91)

```csharp
// REMOVE — the "Unified Opcode Table" comment block and all private opcode constants
// Unified Opcode Table (as of October 28, 2025)
// ...
private const byte RLE_ZeroRun = 0x00;
// ... through ...
private const byte RLE_Planar = 0x0A;
// Reserve 0x0B+ for future ...
```

#### Motif Tuning Constants (lines 93–103)

```csharp
// REMOVE — duplicates DeltaUtils.MotifProbeCount, MotifUnitSizes, etc.
private const int MotifProbeCount = 7;
private static readonly int[] MotifUnitSizes = { 4, 8, 2, 3, 5, 6, 7 };
private static readonly uint[] MotifUnitMods = { ... };
private static readonly float MotifDensityThreshold = 0.7f;
private const float MotifSavingsThreshold = -0.5f;
private const int MotifMinStreak = 2;
private const int MaxMotifStreak = 50;
```

#### #region SIMD Helpers (lines 186–311)

```csharp
// REMOVE entire region — duplicates DeltaUtils.UseSIMD, WriteXORDelta,
//                         ApplyXORDelta, VectorCopy
#region SIMD Helpers
private static bool UseSIMD(...) => ...
private static unsafe void WriteXORDelta(...) { ... }
private static unsafe void ApplyXORDelta(...) { ... }
private static unsafe void VectorCopy(...) { ... }
#endregion
```

#### #region Private Implementation (lines 547–652)

```csharp
// REMOVE entire region — duplicates DeltaUtils.DefaultOptions,
//                         EstimateDeltaSize, EstimateRLESizeForSpan,
//                         Write7BitEncodedInt (3 overloads),
//                         Get7BitEncodedSize, CalculateChangeDensity
#region Private Implementation
private static DeltaOptions DefaultOptions => new() { ... };
private static int EstimateDeltaSize(...) { ... }
private static int EstimateRLESizeForSpan(...) { ... }
private static void Write7BitEncodedInt(IBufferWriter<byte>, int) { ... }
private static int Write7BitEncodedInt(Span<byte>, int) { ... }
private static void Write7BitEncodedInt(BinaryWriter, int) { ... }
private static int Get7BitEncodedSize(int) { ... }
private static double CalculateChangeDensity(...) { ... }
#endregion
```

### 3.2 What Remains After Removal

After removal, `DeltaZor.cs` will contain:
1. `namespace DZ;` and `using` directives
2. Class-level XML doc comment
3. `public static class DeltaZor` with:
   - `DeltaOptions` class
   - `DeltaStats` struct
   - `OpCodeCounts` record struct
   - `DeltaResult<T>` struct
   - `#region Public API` with `CreateDelta`, `ApplyDelta`, `AnalyzeDelta` methods

### 3.3 Resolution After Removal

All references in the public API methods already resolve via `using static DeltaUtils;`:

| Call in DeltaZor.cs | Resolves to |
|---------------------|-------------|
| `DefaultOptions` | `DeltaUtils.DefaultOptions` |
| `HeaderSize` | `DeltaUtils.HeaderSize` |
| `ChecksumSize` | `DeltaUtils.ChecksumSize` |
| `CompressionType_RLE` | `DeltaUtils.CompressionType_RLE` |
| `CompressionType_FullReplace` | `DeltaUtils.CompressionType_FullReplace` |
| `ChecksumFlag` | `DeltaUtils.ChecksumFlag` |
| `CompressionTypeMask` | `DeltaUtils.CompressionTypeMask` |
| `CalculateChangeDensity(...)` | `DeltaUtils.CalculateChangeDensity(...)` |
| `VectorCopy(...)` | `DeltaUtils.VectorCopy(...)` |
| `XxHash32Wrapper.Compute(...)` | `DeltaUtils.XxHash32Wrapper.Compute(...)` |

### 3.4 Unused Usings to Remove

After removing the SIMD helpers and private implementation, these `using` directives in `DeltaZor.cs` become unused and MUST be removed:

```csharp
// REMOVE — no longer needed after dead code removal
using System.IO;                    // Was used by Write7BitEncodedInt(BinaryWriter)
using System.Numerics;              // Was used by SIMD helpers
using System.Runtime.Intrinsics;    // Was used by SIMD helpers (Vector128)
```

Keep:
```csharp
using System.Buffers;               // Used by CreateDelta (ArrayBufferWriter<byte>, MemoryPool<byte>)
using System.Collections.Generic;   // May be needed by public types
using System;                       // Used everywhere
using static DeltaUtils;
using static DeltaEncoder;
using static DeltaDecoder;
```

---

## 4. DefaultOptions Fix

### 4.1 Problem

`DeltaUtils.DefaultOptions` (Utils.cs line 121) returns:
```csharp
public static DeltaZor.DeltaOptions DefaultOptions => new() { UseSIMD = true, CompressionThreshold = 2.0 };
```

`CompressionThreshold = 2.0` means the RLE result must exceed 2x raw data size before falling back to FullReplace. This is effectively "always use RLE" — an undocumented hack.

The `DeltaOptions` class default is `CompressionThreshold = 0.95`, which is the correct documented behavior.

### 4.2 Fix

Change `DeltaUtils.DefaultOptions` (Utils.cs line 121) to:
```csharp
public static DeltaZor.DeltaOptions DefaultOptions => new() { UseSIMD = true, CompressionThreshold = 0.95 };
```

The private `DeltaZor.DefaultOptions` (DeltaZor.cs line 549) is deleted as part of dead code removal (section 3).

### 4.3 Impact

With `CompressionThreshold = 0.95`, the `CreateDelta` fallback check (`dataSpan.Length > newData.Length * 1.5`) is already the binding constraint. The `0.95` value aligns `DefaultOptions` with the `DeltaOptions` class default, eliminating confusion.

Note: Some tests that previously relied on RLE always being used (because `2.0` effectively forces RLE) may now see FullReplace chosen for high-density data. This is correct behavior — the affected tests already assert `Assert.False(stats.UsedRLE)`, confirming FullReplace was expected.

---

## 5. Test Fix Specification

### 5.1 ChecksumAndIntegrityTests.Checksum_Validation_Works

**File:** `src/csharp/DeltaZorTests/UnitTests/ChecksumAndIntegrityTests.cs`

**Problem:** The test creates a delta with default options (`EnableChecksum = false`), corrupts the last byte (which is data, not a checksum), then expects a checksum error. Additionally, the assertion checks for `"Checksum validation failed"` but `ApplyDelta` returns `"Checksum mismatch"`.

**Fix:**
```csharp
[Fact]
public void Checksum_Validation_Works()
{
    // Arrange
    var oldData = new byte[] { 1, 2, 3, 4, 5 };
    var newData = new byte[] { 1, 9, 3, 4, 5 };
    var options = new DeltaZor.DeltaOptions { EnableChecksum = true };  // CHANGED
    var delta = DeltaZor.CreateDelta(oldData, newData, options, out var stats);

    // Corrupt the checksum (last 4 bytes)
    if (delta.Length >= 4)
    {
        delta[^1] ^= 0xFF;
    }

    // Act
    var output = new byte[newData.Length];
    var result = DeltaZor.ApplyDelta(oldData.AsSpan(), delta.AsSpan(), output.AsSpan(), out _);

    // Assert
    Assert.False(result.Success);
    Assert.Contains("Checksum mismatch", result.Error);  // CHANGED: correct error message
}
```

### 5.2 MotifTests — Remove Incorrect UsedRLE Assertions

**File:** `src/csharp/DeltaZorTests/UnitTests/MotifTests.cs`

Four tests assert `Assert.False(stats.UsedRLE)` incorrectly. Whether RLE is used depends on compression threshold and data characteristics — not on motif non-emission. Remove each assertion:

| Test Method | Line | Action |
|-------------|------|--------|
| `MotifDetection_MinStreakNotMet_NoEmission` | 118 | Remove `Assert.False(stats.UsedRLE);` |
| `MotifDetection_HighDensity_NoEmission` | 189 | Remove `Assert.False(stats.UsedRLE);` |
| `MotifDisabled_FallsBackToRLE_NoMotifCounts` | 401 | Remove `Assert.False(stats.UsedRLE);` |
| `MotifTrigger_HighDensity_NoEmit` | 544 | Remove `Assert.False(stats.UsedRLE);` |

Keep all other assertions (no-motif counts, application verification).

### 5.3 MotifTests — Remove Duplicated _output.WriteLine

**File:** `src/csharp/DeltaZorTests/UnitTests/MotifTests.cs`, lines 91–92

**Problem:** Two identical `_output.WriteLine("Output: " + string.Join(", ", output));` calls.

**Fix:** Remove line 92 (the duplicate).

### 5.4 MotifTests — Remove Duplicate changedPositions Assignment

**File:** `src/csharp/DeltaZorTests/UnitTests/MotifTests.cs`, method `MotifDetection_Parameterized_UnitSizesAndRepeats_EmitsCorrectMotif`

**Problem:** Lines 32–35 assign `changedPositions`, then lines 36–39 reassign it identically.

**Fix:** Remove the dead initial assignment on line 31 (`int[] changedPositions = new[] { 1, unitSize - 1 }.Distinct().ToArray();`) and remove the duplicate reassignment on lines 36–39.

**Result after fix:**
```csharp
int[] changedPositions;
if (isUniform)
    changedPositions = new int[] { 0 };
else
    changedPositions = new int[] { 1 };
```

---

## 6. Zig SHA256 Zero-Padding Fix

### 6.1 Problem

**File:** `src/zig/src/tests.zig`, function `computeSha256Hex` (line 34)

```zig
const hex = try fmt.bufPrint(&hex_buf, "{x}", .{digest});
```

The `{x}` format specifier for a `[32]u8` array does not guarantee zero-padded hex output. A byte value `0x0A` formats as `"a"` (1 char) instead of `"0a"` (2 chars), producing a hex string shorter than 64 characters.

### 6.2 Fix

Replace with `std.fmt.fmtSliceHexLower`:

```zig
fn computeSha256Hex(data: []const u8, allocator: mem.Allocator) ![]u8 {
    var hasher = Sha256.init(.{});
    hasher.update(data);
    const digest = hasher.finalResult();
    var hex_buf: [64]u8 = undefined;
    const hex = try fmt.bufPrint(&hex_buf, "{s}", .{std.fmt.fmtSliceHexLower(&digest)});
    return allocator.dupe(u8, hex);
}
```

This guarantees each byte produces exactly 2 hex characters, always yielding a 64-character string.

---

## 7. Build System: net9.0 to net10.0

### 7.1 .csproj Files

Change `<TargetFramework>net9.0</TargetFramework>` to `<TargetFramework>net10.0</TargetFramework>` in:

| File | Current | Target |
|------|---------|--------|
| `src/csharp/DeltaZor/DeltaZor.csproj` | net9.0 | net10.0 |
| `src/csharp/DeltaZorTests/DeltaZorTests.csproj` | net9.0 | net10.0 |
| `src/csharp/DeltaZor.Shared/DeltaZor.Shared.csproj` | net9.0 | net10.0 |
| `src/csharp/DeltaZor.TestGen/DeltaZor.TestGen.csproj` | net9.0 | net10.0 |

### 7.2 build.zig

**File:** `src/zig/build.zig`, line 46

Change the `net9.0` reference in the test data generation command:

```
net9.0\\testdata\\*
```
to:
```
net10.0\\testdata\\*
```

### 7.3 DeltaZorTestDataBenchmarks.cs

**File:** `src/csharp/DeltaZorTests/Benchmarks/DeltaZorTestDataBenchmarks.cs`, lines 132–133

Change:
```csharp
Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Debug", "net9.0", "testdata"),
Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Release", "net9.0", "testdata"),
```
To:
```csharp
Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Debug", "net10.0", "testdata"),
Path.Combine(srcDir, "csharp", "DeltaZor.TestGen", "bin", "Release", "net10.0", "testdata"),
```

---

## 8. File Impact Summary

| File | Changes |
|------|---------|
| `src/csharp/DeltaZor/DeltaZor.cs` | Remove ~280 lines of dead code (private constants, SIMD helpers, private implementation); remove 3 unused `using` directives |
| `src/csharp/DeltaZor/Utils.cs` | Fix `DefaultOptions.CompressionThreshold` from `2.0` to `0.95` (line 121) |
| `src/csharp/DeltaZor/DeltaZor.csproj` | `net9.0` to `net10.0` |
| `src/csharp/DeltaZorTests/DeltaZorTests.csproj` | `net9.0` to `net10.0` |
| `src/csharp/DeltaZor.Shared/DeltaZor.Shared.csproj` | `net9.0` to `net10.0` |
| `src/csharp/DeltaZor.TestGen/DeltaZor.TestGen.csproj` | `net9.0` to `net10.0` |
| `src/csharp/DeltaZorTests/UnitTests/ChecksumAndIntegrityTests.cs` | Enable checksum in test; fix error message assertion |
| `src/csharp/DeltaZorTests/UnitTests/MotifTests.cs` | Remove 4 incorrect `Assert.False(stats.UsedRLE)` assertions; remove duplicate `_output.WriteLine`; remove duplicate `changedPositions` assignment |
| `src/csharp/DeltaZorTests/Benchmarks/DeltaZorTestDataBenchmarks.cs` | `net9.0` to `net10.0` in test data paths |
| `src/zig/src/tests.zig` | Fix `computeSha256Hex` to use `fmtSliceHexLower` |
| `src/zig/build.zig` | `net9.0` to `net10.0` in test data generation command |

---

## 9. Verification

After all changes, the following MUST pass:

1. `dotnet build src/csharp/DeltaZor/DeltaZor.csproj` — clean build, no warnings about shadowed members
2. `dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj` — all tests green
3. `cd src/zig && zig build test` — all Zig tests green (requires test data)
4. Manual inspection: `DeltaZor.cs` contains zero `private` methods and zero `private const` fields

---

## 10. Non-Goals

- No changes to the wire format (section 2 of prior architecture doc)
- No changes to `Encoder.cs` or `Decoder.cs`
- No changes to Zig encoder/decoder logic
- No new features or API changes
- No package version bumps (except target framework)
