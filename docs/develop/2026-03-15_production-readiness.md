# Development Plan: DeltaZor Production Readiness

**Date:** 2026-03-15
**Status:** In Progress
**Author:** Develop Agent

## Architecture Document

`docs/architecture/2026-03-15_production-readiness.md`

## Related Documents

- Plan.md (project roadmap)
- MOTIF_REPEAT_OPCODES.md
- PATTERN_COUNTS_FEATURE.md
- SUMMARY.md

## Objective

DeltaZor is a high-performance dual-language (C# + Zig) binary delta compression library. The codebase has significant architectural and correctness issues that prevent production use. This plan addresses all blocking issues: opcode format divergence between C# and Zig, broken checksum validation, encoder/decoder bugs, test failures, code duplication, and missing production hardening. The goal is a fully passing test suite and a production-ready library.

## Issues Identified

### Critical (Blocking Production Use)

1. **C# ↔ Zig Opcode Format Divergence**: The C# encoder emits opcodes 0x04/0x05 for motifs with a specific format (flags byte + varint repeat + varint unit + optional mask + data). The Zig encoder emits entirely different opcodes (0x80-0x87, 0x88, 0xA0-0xA2) with different formats. The Zig decoder handles both C# legacy (0x04/0x05) AND its own new opcodes, but the C# decoder only handles 0x04/0x05. This means deltas produced by Zig cannot be applied by C#, breaking cross-language interoperability — a core design goal.

2. **Broken Checksum Validation in C#**: `DeltaOptions.EnableChecksum` defaults to `false`, but `ApplyDelta` uses `DefaultOptions` (which has `EnableChecksum = false`) regardless of what options were used to create the delta. The `ChecksumAndIntegrityTests.Checksum_Validation_Works` test expects checksum validation to work but it never will because the apply path ignores the checksum. The test corrupts the last byte and expects failure — but since checksums are disabled by default, it will always succeed (wrong behavior).

3. **C# Encoder `PackChangedPositionsForVarying` is Empty**: The method body is completely empty (`// Omitted for brevity`). This means varying motif encoding is broken — it will write garbage/uninitialized data for varying masked motifs.

4. **C# `EmitOps` Extension Case is Incomplete**: The `OpType.Extension` case in `EmitOps` writes the opcode and length but has a comment "Assume handled in caller" and does NOT write the actual extension data. This means extension data is silently dropped in the motif path.

5. **C# `BuildRleRuns` throws on >256 runs**: `throw new InvalidOperationException("Too many RLE runs for buffer")` — this is a hard crash for any data with more than 256 alternating zero/non-zero runs. Production data (e.g., 1KB+ buffers with scattered changes) will easily exceed this.

6. **C# `BuildBasicOps` and `BuildMotifOps` throw on >256 ops**: Same issue — hard crashes for real-world data.

7. **Zig `emitFromRLEBuffer` double-writes opcode for `RLE_MEDIUM_MOTIF`**: The opcode is written once in the outer loop (`output[data_pos.*] = entry.opcode; data_pos.* += 1;`) and then again inside the `RLE_MEDIUM_MOTIF` case (`output[data_pos.*] = entry.opcode; data_pos.* += 1;`). This corrupts the output stream.

8. **Zig `optimizeMotifsInBuffer` is not called for `large_streaming` path**: The `optimized_len` is computed from `optimizeMotifsInBuffer` but the function is called with `buffer_pos` which is 0 for the large_streaming path (since that path writes directly to output, bypassing the buffer). This means the optimization pass runs on an empty buffer.

9. **Zig `createRLEDeltaDirect` buffer overflow risk**: `rle_buffer: [8192]utils.RLEEntry` on the stack — for large files with many runs, this will overflow. The `@panic` at the end only fires after the overflow has already occurred.

10. **C# `ApplyDelta` uses `DefaultOptions` hardcoded**: The apply path ignores the options passed to create. This means if a delta was created with `EnableChecksum = true`, applying it with `ApplyDelta` will fail to validate the checksum (since `DefaultOptions.EnableChecksum = false`).

### Significant (Test Failures / Correctness)

11. **`ChecksumAndIntegrityTests.Checksum_Validation_Works` will fail**: The test corrupts the delta and expects `result.Success == false` with error containing "Checksum validation failed". But since `EnableChecksum` defaults to false and `ApplyDelta` uses `DefaultOptions`, the checksum is never checked. The test will pass (wrong reason) or fail depending on whether the corruption happens to corrupt the RLE data itself.

12. **`MotifTests.MotifDetection_MinStreakNotMet_NoEmission` asserts `Assert.False(stats.UsedRLE)`**: This test expects that for a 4-byte buffer with 2 changes, the system falls back to FullReplace. But the current logic always tries RLE first and only falls back if `rle_data > newData.Length * 1.5`. For 4 bytes, RLE overhead is significant but may not exceed 1.5x. This test is fragile.

13. **`MotifTests.MotifDetection_HighDensity_NoEmission` asserts `Assert.False(stats.UsedRLE)`**: Same issue — expects FullReplace for 16-byte high-density data.

14. **`MotifTests.MotifDisabled_FallsBackToRLE_NoMotifCounts` asserts `Assert.False(stats.UsedRLE)`**: Same pattern.

15. **`MotifTests.MotifTrigger_HighDensity_NoEmit` asserts `Assert.False(stats.UsedRLE)`**: Same.

16. **C# `DeltaZor.cs` has duplicate code from `Utils.cs`**: `DeltaZor.cs` contains its own private copies of `Write7BitEncodedInt`, `Get7BitEncodedSize`, `CalculateChangeDensity`, `EstimateRLESizeForSpan`, `WriteXORDelta`, `ApplyXORDelta`, `VectorCopy`, `Crc32`, `MotifCandidate`, `ChannelPattern` — all of which also exist in `Utils.cs`. The `DeltaZor.cs` versions are not used (the encoder/decoder use `DeltaUtils.*`), creating dead code and maintenance confusion.

17. **`StrategySelectionTests.EstimateRLESize_CalculatesAccurateSizeEstimates` expects `checksumSize = 4`**: The test subtracts 4 bytes for checksum from the delta, but `EnableChecksum` defaults to `false`, so there are 0 checksum bytes. The test will compute a wrong `actualRleSize`.

18. **Zig `computeSha256Hex` uses `{x}` format which may not zero-pad**: SHA256 produces 32 bytes; `{x}` may omit leading zeros, producing a hex string shorter than 64 chars. The C# side uses `BitConverter.ToString(...).Replace("-","").ToLowerInvariant()` which always produces 64 chars. This will cause checksum mismatches in cross-language test validation.

19. **`TestGenTests` `ranGenerateTestData` logic is broken**: `ranGenerateTestData = DateTime.Today` (midnight today), and the check is `ranGenerateTestData < DateTime.Now.Subtract(TimeSpan.FromMinutes(10))`. On the first run of the day, `DateTime.Today` is midnight, and `DateTime.Now.Subtract(10 minutes)` is ~current time minus 10 min. Since midnight < (now - 10min), it will always regenerate. This is likely intentional but the logic is confusing and will regenerate on every test run.

20. **C# `DeltaZor.cs` `DefaultOptions` has `CompressionThreshold = 2.0`**: This means "always use RLE" (threshold is never exceeded since ratio can't exceed 2.0 in practice). But `DeltaOptions` doc says "Default is 0.95 (50% size reduction required)". The actual default in `DeltaOptions` constructor is `0.95`. The `DefaultOptions` static property overrides this with `2.0`, which is inconsistent and undocumented.

### Minor (Code Quality / Maintainability)

21. **`MotifTests` has duplicated `_output.WriteLine` calls**: Lines like `_output.WriteLine("Output: ...")` appear twice in sequence in several tests.

22. **`MotifTests.MotifDetection_Parameterized_UnitSizesAndRepeats_EmitsCorrectMotif`**: The `changedPositions` assignment is duplicated (lines 32-39 assign it twice with identical logic).

23. **Zig `crc32` recomputes the lookup table on every call**: The table should be computed once (comptime or static), not on every invocation.

24. **C# `DeltaZor.cs` has `unsafe` keyword on methods that don't use unsafe code**: `WriteXORDelta`, `ApplyXORDelta`, `VectorCopy` are marked `unsafe` but don't use any unsafe constructs.

25. **Missing `docs/` directory structure**: No architecture or development docs exist yet.

## Steps

### STEP-01: Architecture Design
- **Agent:** @architect
- **Status:** DONE
- **Input:** This plan document, full codebase analysis above
- **Output:** Architecture document at docs/architecture/2026-03-15_production-readiness.md
- **Result:** Architecture document written. Decisions: opcode unification on 0x04/0x05; checksum self-description via flag bit in compression_type; dynamic buffer growth replacing fixed-256 stackalloc; PackChangedPositionsForVarying implemented; Extension case removed from EmitOps; comptime CRC32 table in Zig; SHA256 zero-padding fix; DefaultOptions consistency.

### STEP-02: Fix Critical C# Bugs (Encoder Correctness)
- **Agent:** @coder
- **Status:** DONE
- **Input:** Plan (STEP-02), Architecture doc
- **Tasks:**
  - Fix `PackChangedPositionsForVarying` — implement the actual packing logic ✅
  - Fix `EmitOps` Extension/Truncation cases — throw InvalidOperationException (handled in CreateRLEDelta) ✅
  - Fix `BuildRleRuns` / `BuildBasicOps` / `BuildMotifOps` — converted to `List<T>`, no hard 256-limit ✅
  - Remove dead code from `DeltaZor.cs` (duplicate private methods that shadow `DeltaUtils`) ✅
  - Fix `DefaultOptions.CompressionThreshold` from 2.0 to 0.95 ✅
- **Output:** Affected files list
- **Affected Files:**
  - `src/csharp/DeltaZor/Encoder.cs` (modified)
  - `src/csharp/DeltaZor/DeltaZor.cs` (modified)
  - `src/csharp/DeltaZor/Utils.cs` (modified)
- **Result:** APPROVED by reviewer

### STEP-03: Review of STEP-02
- **Agent:** @reviewer
- **Status:** DONE
- **Input:** Plan (STEP-02), Architecture doc, Affected files
- **Output:** APPROVED
- **Result:** APPROVED

### STEP-04: Fix Checksum Handling (C#)
- **Agent:** @coder
- **Status:** DONE
- **Input:** Plan (STEP-04), Architecture doc
- **Tasks:**
  - Add `ChecksumFlag = 0x80` and `CompressionTypeMask = 0x7F` constants to `Utils.cs` ✅
  - Fix `CreateDelta` to write checksum flag into header bit 7 ✅
  - Fix `ApplyDelta` to read checksum flag from header (self-describing) ✅
  - Fix `ChecksumAndIntegrityTests.Checksum_Validation_Works` to use `EnableChecksum = true` ✅
  - Fix `StrategySelectionTests` checksum size detection to read from delta header ✅
  - Fix `DefaultOptions` to use `CompressionThreshold = 0.95` ✅
- **Output:** Affected files list
- **Affected Files:**
  - `src/csharp/DeltaZor/Utils.cs` (modified — ChecksumFlag, CompressionTypeMask constants)
  - `src/csharp/DeltaZor/DeltaZor.cs` (modified — CreateDelta writes flag, ApplyDelta reads flag)
  - `src/csharp/DeltaZorTests/UnitTests/ChecksumAndIntegrityTests.cs` (modified)
  - `src/csharp/DeltaZorTests/UnitTests/StrategySelectionTests.cs` (modified)
- **Result:** Implemented. Pending review (STEP-05).

### STEP-05: Review of STEP-04
- **Agent:** @reviewer
- **Status:** PENDING
- **Input:** Plan (STEP-04), Architecture doc, Affected files
- **Output:** APPROVED or issues list
- **Result:** _filled in after completion_

### STEP-06: Fix Zig Encoder Bugs
- **Agent:** @coder
- **Status:** PENDING
- **Input:** Plan (STEP-06), Architecture doc
- **Tasks:**
  - Fix double-write of opcode in `emitFromRLEBuffer` for `RLE_MEDIUM_MOTIF` case
  - Fix `optimizeMotifsInBuffer` not being called for large_streaming path (or document that it's intentionally skipped)
  - Fix `crc32` to use a static/comptime lookup table instead of recomputing on every call
  - Fix `computeSha256Hex` zero-padding issue in tests.zig
- **Output:** Affected files list
- **Affected Files:** _filled in after completion_
- **Result:** _filled in after completion_

### STEP-07: Review of STEP-06
- **Agent:** @reviewer
- **Status:** PENDING
- **Input:** Plan (STEP-06), Architecture doc, Affected files
- **Output:** APPROVED or issues list
- **Result:** _filled in after completion_

### STEP-08: Unify Opcode Format (C# ↔ Zig Interoperability)
- **Agent:** @coder
- **Status:** PENDING
- **Input:** Plan (STEP-08), Architecture doc
- **Tasks:**
  - Standardize on the C# opcode format (0x04 uniform motif, 0x05 varying motif) as the canonical wire format
  - Update Zig encoder to emit 0x04/0x05 format (matching C# exactly)
  - Update Zig decoder to handle 0x04/0x05 as primary (keep legacy 0x80-0xA2 as deprecated/removed)
  - Update C# decoder to handle any new opcodes if the Zig side introduces them
  - Ensure test vectors (testdata/) are regenerated after format unification
  - Update `DeltaUtils.cs` opcode constants to match
- **Output:** Affected files list
- **Affected Files:** _filled in after completion_
- **Result:** _filled in after completion_

### STEP-09: Review of STEP-08
- **Agent:** @reviewer
- **Status:** PENDING
- **Input:** Plan (STEP-08), Architecture doc, Affected files
- **Output:** APPROVED or issues list
- **Result:** _filled in after completion_

### STEP-10: Fix Failing Tests
- **Agent:** @coder
- **Status:** PENDING
- **Input:** Plan (STEP-10), Architecture doc
- **Tasks:**
  - Fix `MotifTests` tests that assert `Assert.False(stats.UsedRLE)` — align assertions with actual behavior or fix the fallback logic to correctly fall back to FullReplace for small high-overhead cases
  - Fix duplicated `_output.WriteLine` calls in `MotifTests`
  - Fix duplicated `changedPositions` assignment in `MotifDetection_Parameterized_UnitSizesAndRepeats_EmitsCorrectMotif`
  - Fix `TestGenTests` `ranGenerateTestData` logic to be clearer
  - Ensure all non-skipped tests pass
- **Output:** Affected files list
- **Affected Files:** _filled in after completion_
- **Result:** _filled in after completion_

### STEP-11: Review of STEP-10
- **Agent:** @reviewer
- **Status:** PENDING
- **Input:** Plan (STEP-10), Architecture doc, Affected files
- **Output:** APPROVED or issues list
- **Result:** _filled in after completion_

### STEP-12: Final Expert Review
- **Agent:** @expert
- **Status:** PENDING
- **Input:** Plan file, Architecture doc, git working tree
- **Output:** APPROVED or issues list
- **Result:** _filled in after completion_

## Summary

_Filled in at completion_

## Affected Files (All)

### C# Core Library
- `src/csharp/DeltaZor/DeltaZor.cs` — dead code removed, checksum self-description, DefaultOptions fixed
- `src/csharp/DeltaZor/Encoder.cs` — PackChangedPositionsForVarying implemented, List<T> buffers, EmitOps fixed
- `src/csharp/DeltaZor/Utils.cs` — ChecksumFlag/CompressionTypeMask constants added

### C# Tests
- `src/csharp/DeltaZorTests/UnitTests/ChecksumAndIntegrityTests.cs` — EnableChecksum=true fix
- `src/csharp/DeltaZorTests/UnitTests/StrategySelectionTests.cs` — checksum size detection fix

### Zig (Pending)
- `src/zig/src/encoder.zig`
- `src/zig/src/decoder.zig`
- `src/zig/src/utils.zig`
- `src/zig/src/tests.zig`