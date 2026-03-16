# Architecture: DeltaZor Production Readiness

**Date:** 2026-03-15
**Status:** Approved (Revision 2)
**Author:** Architect Agent

---

## 1. Overview

DeltaZor is a dual-language binary delta compression library implemented in both C# (.NET 9) and Zig. It encodes the difference between two byte buffers ("old" and "new") as a compact delta stream that can be applied to old data to reproduce new data. The library targets high-performance, low-allocation operation for game state synchronization, binary asset versioning, and network patching scenarios.

**Core invariant:** A delta produced by C# MUST be decodable by Zig, and vice versa. Both encoders MUST produce byte-identical output for identical input and options.

---

## 2. Canonical Wire Format

All deltas — regardless of which language produced them — MUST conform to this canonical wire format. There are no language-specific opcodes or extensions.

### 2.1 Delta Header

```
┌─────────────────────────┬──────────────────────────┬─────────┬──────────────────┐
│ output_length : 4 bytes │ compression_type : 1 byte│ data... │ checksum? : 4 B  │
│ (little-endian uint32)  │ (see §2.2)               │         │ (see §2.2)       │
└─────────────────────────┴──────────────────────────┴─────────┴──────────────────┘
```

- **output_length** (4 bytes, LE uint32): The expected byte length of the decoded output.
- **compression_type** (1 byte): Encodes both the compression algorithm and the checksum-present flag (see §2.2).
- **data** (variable): The opcode stream (RLE mode) or raw replacement data (FullReplace mode).
- **checksum** (4 bytes, LE uint32, conditional): CRC32 of the final decoded output. Present ONLY when the checksum flag is set in `compression_type`.

Total header size: 5 bytes (output_length + compression_type). Minimum valid delta: 5 bytes (no data, no checksum).

### 2.2 Checksum Self-Description

The `compression_type` byte carries a checksum-present flag in its high bit:

```
  Bit 7 (0x80): Checksum present flag
  Bits 6-0:     Compression type
```

| Byte Value | Compression Type | Checksum Present |
|------------|-----------------|------------------|
| `0x00`     | RLE Delta       | No               |
| `0x80`     | RLE Delta       | Yes              |
| `0x01`     | Full Replace    | No               |
| `0x81`     | Full Replace    | Yes              |

**Encoder behavior:** When `EnableChecksum = true`, the encoder writes `compression_type | 0x80` and appends a 4-byte CRC32 of the **final decoded output** (i.e., `newData`) after the data section. When `EnableChecksum = false`, the encoder writes the plain compression type and omits the checksum entirely (no trailing 4 bytes).

**Decoder behavior:** The decoder reads `checksum_present = (compression_type & 0x80) != 0` and `base_type = compression_type & 0x7F`. If `checksum_present`, the last 4 bytes of the delta are the CRC32 checksum, and the decoder validates after reconstruction. If not present, the data section extends to the end of the delta.

**Key property:** The `ApplyDelta` API does NOT need a `DeltaOptions` parameter for checksum behavior — the delta is self-describing.

### 2.3 RLE Opcode Stream

All counts use 7-bit variable-length integer encoding (LEB128 unsigned):
- Continuation bit: `byte & 0x80 != 0` means more bytes follow.
- Value bits: `byte & 0x7F` are the payload, least significant group first.
- Maximum encoded value: 2^35 - 1 (5 bytes).

| Opcode | Name              | Format                                          | Description |
|--------|-------------------|-------------------------------------------------|-------------|
| `0x00` | ZeroRun           | `[0x00][count:varint]`                          | `count` bytes unchanged — skip/copy from old data |
| `0x01` | NonZeroRun        | `[0x01][count:varint][xor_data:count]`          | XOR `count` bytes of old data with `xor_data` |
| `0x02` | Extension         | `[0x02][count:varint][new_data:count]`          | Append `count` new bytes (data grew) |
| `0x03` | Truncation        | `[0x03][new_length:varint]`                     | Output is truncated to `new_length` bytes |
| `0x04` | UniformMotifRepeat| `[0x04][flags:1][repeat:varint][unit:varint][mask?:varint][xor_data:N]` | Repeating pattern, same XOR values each repeat (§2.4) |
| `0x05` | VaryingMotifRepeat| `[0x05][flags:1][repeat:varint][unit:varint][mask?:varint][xor_data:N]` | Repeating pattern, different XOR values per repeat (§2.4) |

**Reserved opcodes:** `0x06`–`0x0A` are reserved for future use (FloatRun, HalfRun, ChannelRun, Arithmetic, Planar). `0x0B`–`0x7F` are reserved. `0x80`–`0xFF` are **prohibited** — the Zig-specific compact motif opcodes (`0x80`–`0xA2`) are deprecated and MUST NOT be emitted by either encoder.

### 2.4 Motif Opcode Format (0x04 / 0x05)

```
┌──────────┬──────────┬────────────────┬──────────────┬───────────────┬──────────────┐
│ opcode:1 │ flags:1  │ repeat:varint  │ unit:varint  │ mask?:varint  │ xor_data:N   │
│ 0x04|0x05│ see below│ ≥2             │ 1..32        │ if masked     │ see below    │
└──────────┴──────────┴────────────────┴──────────────┴───────────────┴──────────────┘
```

**Flags byte:**
- `0x00` = Full mode (no mask field; all positions in the unit changed)
- `0x80` = Masked mode (mask field follows; only positions with mask bit set changed)

**Mask encoding:** A bitmask where bit `i` being set means byte position `i` within the unit has changed. Bit 0 = first byte of unit. Encoded as a varint. `mask != 0` in masked mode.

**changed_count:**
- Full mode: `changed_count = unit_size`
- Masked mode: `changed_count = popcount(mask)`

**xor_data size (N):**
- Uniform (0x04): `N = changed_count` — the XOR values for the first unit; all repeats apply the same XOR values.
- Varying (0x05): `N = changed_count × repeat_length` — XOR values for each repeat's changed positions, in row-major order (repeat 0's changed bytes first, then repeat 1's, etc.).

**Application algorithm (decoder):**
```
pos_list = [i for i in 0..unit_size if (full or mask bit i is set)]
for r in 0..repeat_length:
    for j in 0..changed_count:
        if uniform:
            output[pos + r * unit_size + pos_list[j]] ^= xor_data[j]
        else:  // varying
            output[pos + r * unit_size + pos_list[j]] ^= xor_data[r * changed_count + j]
pos += unit_size * repeat_length
```

**Validity constraints:**
- `repeat_length >= 2` (minimum streak to justify motif overhead)
- `1 <= unit_size <= 32`
- `mask != 0` when in masked mode
- `popcount(mask) <= unit_size`

---

## 3. Module Architecture

### 3.1 C# Layer (`src/csharp/DeltaZor/`)

```
DZ namespace
├── DeltaZor.cs      — Public API ONLY (static class DeltaZor)
│   ├── DeltaOptions          — Configuration record
│   ├── DeltaStats            — Compression statistics
│   ├── DeltaResult<T>        — Result wrapper
│   ├── OpCodeCounts          — Opcode emission counters
│   ├── CreateDelta(...)      — Public encode entry points
│   ├── ApplyDelta(...)       — Public decode entry point
│   └── AnalyzeDelta(...)     — Analysis without encoding
│
├── Encoder.cs       — Encoding pipeline (static class DeltaEncoder)
│   ├── CreateRLEDelta(old, new, writer, options) → OpCodeCounts
│   ├── [private] BuildRleRuns(xorData) → List<RleRun>
│   ├── [private] BuildBasicOps(rleRuns) → List<BasicOp>
│   ├── [private] BuildMotifOps(basicOps, xorData, options) → List<BasicOp>
│   ├── [private] EmitOps(motifOps, xorData, writer)
│   ├── [private] FindMotifCandidate(xorData, startPos, options)
│   ├── [private] PackChangedPositions(unitData, mask, isFull, packed)
│   └── [private] PackChangedPositionsForVarying(xorData, offset, mask, isFull, unitSize, repeatLength, packed)
│
├── Decoder.cs       — Decoding pipeline (static class DeltaDecoder)
│   └── ApplyRLEDelta(old, delta, output, options) → bool
│
└── Utils.cs         — Shared constants and utilities (static class DeltaUtils)
    ├── Opcode constants (RLE_ZeroRun through RLE_VaryingMotifRepeat)
    ├── Header constants (HeaderSize, ChecksumSize, ChecksumFlag)
    ├── Motif tuning constants
    ├── SIMD helpers (WriteXORDelta, ApplyXORDelta, VectorCopy)
    ├── 7-bit varint helpers (Write7BitEncodedInt, Get7BitEncodedSize)
    ├── Crc32 (static lookup table, Compute method)
    ├── SpanReader (ref struct for delta parsing)
    ├── MotifCandidate, ChannelPattern (internal structs)
    └── DefaultOptions, CalculateChangeDensity, EstimateRLESizeForSpan
```

**Invariant:** `DeltaZor.cs` MUST NOT contain private duplicates of ANY method or type defined in `DeltaUtils`, `DeltaEncoder`, or `DeltaDecoder`. All internal logic lives in those modules. `DeltaZor.cs` contains only public API wrappers and public data types.

**Dead code to remove from DeltaZor.cs:**
- Private `Write7BitEncodedInt` (3 overloads) → use `DeltaUtils.Write7BitEncodedInt`
- Private `Get7BitEncodedSize` → use `DeltaUtils.Get7BitEncodedSize`
- Private `CalculateChangeDensity` → use `DeltaUtils.CalculateChangeDensity`
- Private `EstimateRLESizeForSpan` → use `DeltaUtils.EstimateRLESizeForSpan`
- Private `WriteXORDelta` → use `DeltaUtils.WriteXORDelta`
- Private `ApplyXORDelta` → use `DeltaUtils.ApplyXORDelta`
- Private `VectorCopy` → use `DeltaUtils.VectorCopy`
- Private `Crc32` class → use `DeltaUtils.Crc32`
- Private `MotifCandidate` struct → use `DeltaUtils.MotifCandidate`
- Private `ChannelPattern` struct → use `DeltaUtils.ChannelPattern`
- Private `EstimateDeltaSize` → use `DeltaUtils.EstimateDeltaSize`
- Private `DefaultOptions` property → use `DeltaUtils.DefaultOptions`
- Private `UseSIMD` method → use `DeltaUtils.UseSIMD`
- Remove `unsafe` keyword from methods being deleted

### 3.2 Zig Layer (`src/zig/src/`)

```
├── deltazor.zig    — Public API
│   ├── DeltaZor.Options (re-export from utils)
│   ├── DeltaZor.Stats (re-export from utils)
│   ├── DeltaZor.createDelta(old, new, allocator, options) → []u8
│   └── DeltaZor.applyDelta(old, delta, output, allocator) → void
│
├── encoder.zig     — RLE/motif encoding pipeline
│   ├── createDeltaWithStats(old, new, allocator, options, stats)
│   ├── [private] createRLEDeltaDirect(old, new, buffer, pos, options, counts)
│   ├── [private] buildBasicRLEBuffer(xor, old, new, buffer, pos, options, counts)
│   ├── [private] optimizeMotifsInBuffer(buffer, len, xor, old, new, counts)
│   ├── [private] emitFromRLEBuffer(buffer, len, xor, old, new, output, pos, counts)
│   └── [private] findMotifCandidate(xor, old, new, pos, options)
│
├── decoder.zig     — RLE/motif decoding pipeline
│   └── applyDelta(old, delta, output, allocator) → void
│
└── utils.zig       — Shared constants, options, entry types
    ├── Opcode constants (RLE_ZERO_RUN = 0x00 .. RLE_VARYING_MOTIF_REPEAT = 0x05)
    ├── Options, Stats, OpCodeCounts structs
    ├── RLEEntry struct
    ├── crc32(data) → u32 [comptime lookup table]
    ├── readByte, read7bit, write7BitEncodedIntDirect
    ├── get7BitEncodedSize, popCount32
    └── bit_masks [comptime array]
```

---

## 4. Encoding Pipeline

### 4.1 C# Encoder Pipeline

```
CreateDelta (DeltaZor.cs)
  ├─ Calculate change density
  ├─ Attempt RLE via CreateRLEDelta (Encoder.cs)
  │    ├─ [small data, motifs enabled] Full XOR path:
  │    │    Stage 1: BuildRleRuns(xorData) → List<RleRun>
  │    │    Stage 2: BuildBasicOps(rleRuns) → List<BasicOp>
  │    │    Stage 3: BuildMotifOps(basicOps, xorData) → List<BasicOp>
  │    │    Stage 4: EmitOps(motifOps, xorData, writer)
  │    │
  │    └─ [large data] Streaming RLE (no motifs):
  │         Emit ZeroRun/NonZeroRun directly to writer
  │
  │    Then: Emit Extension (0x02) or Truncation (0x03) if lengths differ
  │
  ├─ If RLE data > newData.Length × 1.5: Fall back to FullReplace
  ├─ Write header: [output_length:4][compression_type:1]
  ├─ Write data section
  └─ If checksum enabled: append CRC32 of newData
```

**Buffer management:** Stages 1–3 use `List<T>` for dynamic growth. No fixed-size stackalloc. Pre-allocated with `Math.Max(8, xorData.Length / 16)` initial capacity.

**Extension and Truncation** are emitted directly in `CreateRLEDelta` AFTER the EmitOps call. They are NEVER in the ops list passed to EmitOps. The `EmitOps` method throws `InvalidOperationException` if it encounters an Extension or Truncation op — this is a programming error.

### 4.2 Zig Encoder Pipeline

```
createDeltaWithStats (encoder.zig)
  ├─ Allocate output buffer
  ├─ Write header
  ├─ createRLEDeltaDirect:
  │    ├─ [use_full_xor] Small data with motifs:
  │    │    buildBasicRLEBuffer → optimizeMotifsInBuffer → emitFromRLEBuffer
  │    │
  │    ├─ [large_streaming] Large data (>100KB):
  │    │    Direct emit to output (no intermediate buffer, no motif optimization)
  │    │    Skip optimize+emit calls entirely (buffer_pos is 0)
  │    │
  │    └─ [streaming] Medium data without full XOR:
  │         buildBasicRLEBuffer → optimizeMotifsInBuffer → emitFromRLEBuffer
  │
  │    Then: Emit Extension or Truncation if lengths differ
  │
  ├─ If RLE > newData × 1.5: Fall back to FullReplace
  └─ Write checksum if enabled
```

**Buffer management:** Zig uses fixed stack arrays (`rle_buffer: [8192]RLEEntry`, `temp_buffer: [4096]u8`). These are acceptable for the documented size limits:
- `rle_buffer` supports up to 8192 RLE entries (sufficient for data up to `max_stack_buffer_size = 4096` bytes).
- The `large_streaming` path (>100KB) bypasses the buffer entirely.
- A bounds check (`buffer_pos > 8000`) with `@panic` exists as a safety net but should not trigger in practice.
- **Future improvement:** Use allocator-backed dynamic buffers for the streaming path if data between 4KB and 100KB needs motif optimization.

---

## 5. Key Design Decisions

### 5.1 Opcode Unification (Critical)

**Decision:** Standardize on opcodes `0x04`/`0x05` as the ONLY motif wire format for both C# and Zig.

**Rationale:**
- C# encoder already produces `0x04`/`0x05` with the format: `[opcode:1][flags:1][repeat:varint][unit:varint][mask?:varint][data]`
- C# decoder only understands `0x04`/`0x05`
- Zig decoder already has full `0x04`/`0x05` decode support (labeled "C# legacy" in comments)
- The Zig compact opcodes (`0x80`–`0xA2`) saved a few bytes per motif but broke cross-language compatibility entirely — an unacceptable tradeoff

**Migration path:**
1. Update Zig `findMotifCandidate` to always set opcode to `0x04` (uniform) or `0x05` (varying), regardless of unit size or density.
2. Update Zig `buildBasicRLEBuffer` to assign `0x04`/`0x05` opcodes for motif entries.
3. Update Zig `emitFromRLEBuffer` to handle only `0x00`–`0x05`. Remove all `0x80`–`0xA2` emit paths.
4. Update Zig `optimizeMotifsInBuffer` to assign `0x04`/`0x05` opcodes instead of `0x80`–`0xA2`.
5. Zig decoder: retain `0x80`–`0xA2` decode for one release cycle (backward compatibility), then remove.
6. Zig `utils.zig`: change `RLE_UNIFORM_MOTIF_REPEAT` from `0xA0` to `0x04` and `RLE_VARYING_MOTIF_REPEAT` from `0xA1` to `0x05`. Add deprecated aliases for `0xA0`/`0xA1`.
7. Regenerate all test data using the unified encoder.

### 5.2 Checksum Self-Description

**Decision:** Encode checksum presence as bit 7 (`0x80`) of the `compression_type` byte.

**Encoder changes:**
- C#: `output[4] = (byte)((usedRLE ? 0x00 : 0x01) | (options.EnableChecksum ? 0x80 : 0x00));`
- Zig: `buffer[pos] = (if (used_rle) @as(u8, 0x00) else @as(u8, 0x01)) | (if (options.enable_checksum) @as(u8, 0x80) else @as(u8, 0x00));`

**Decoder changes (both languages):**
```
checksum_present = (compression_type & 0x80) != 0
base_type = compression_type & 0x7F
data_end = if checksum_present then (delta.len - 4) else delta.len
// After decode, if checksum_present: validate CRC32
```

The `ApplyDelta` method no longer uses `DefaultOptions.EnableChecksum` — it reads the flag from the delta header.

### 5.3 Buffer Management — C# (List\<T\>)

**Decision:** Replace ALL fixed-size `stackalloc` staging buffers in the encoder pipeline with `List<T>`.

**What changes:**
- `BuildRleRuns`: returns `List<RleRun>` (already done in current code)
- `BuildBasicOps`: returns `List<BasicOp>` (already done in current code)
- `BuildMotifOps`: returns `List<BasicOp>` (already done in current code)
- Initial capacity: `Math.Max(8, xorData.Length / 16)` for run lists

**What does NOT change:**
- The `SpanReader` in the decoder (stack-based, efficient)
- The 1-byte `oneByteSpan = stackalloc byte[1]` scratch buffers (trivial)
- The `tempBuffer` in `CreateRLEDelta` streaming path (bounded by `MaxStackBufferSize`)

**Rationale:** The original stackalloc-256 approach was premature optimization that crashes on production data. `List<T>` has negligible overhead for the motif-enabled path (data ≤ 4096 bytes, typically < 256 runs).

### 5.4 Buffer Management — Zig (Stack Arrays with Documented Limits)

**Decision:** Zig keeps stack-allocated fixed arrays with documented limits.

- `rle_buffer: [8192]RLEEntry` — supports data up to 4096 bytes (max_stack_buffer_size)
- `temp_buffer: [4096]u8` — matches max_stack_buffer_size
- Large data (>100KB) uses the `large_streaming` path which bypasses buffers entirely
- Medium data (4KB–100KB) uses streaming path with buffer; if data produces >8192 entries, the safety `@panic` fires

**Future:** If medium-data motif detection is needed, replace stack arrays with allocator-backed buffers.

### 5.5 PackChangedPositionsForVarying Fix

**Bug:** The method was completely empty — writing uninitialized/zero data for varying masked motifs.

**Fix:** Implement the packing algorithm:
```csharp
int cursor = 0;
for (int r = 0; r < repeatLength; r++)
{
    int repBase = offset + r * unitSize;
    if (isFull)
    {
        for (int i = 0; i < unitSize; i++)
            packed[cursor++] = xorData[repBase + i];
    }
    else
    {
        for (int i = 0; i < unitSize; i++)
            if ((mask & (1u << i)) != 0)
                packed[cursor++] = xorData[repBase + i];
    }
}
```

**Note:** This has already been implemented in the current codebase. The develop plan's Issue #3 appears to reference an older version. Verify by inspection that the implementation matches the algorithm above.

### 5.6 EmitOps Extension/Truncation Handling

**Decision:** Extension and Truncation ops are NEVER placed in the ops list passed to `EmitOps`. They are emitted directly in `CreateRLEDelta` after the motif pipeline completes.

The `EmitOps` method throws `InvalidOperationException` for `OpType.Extension` and `OpType.Truncation` to catch programming errors. This is already implemented in the current code.

### 5.7 Dead Code Removal from DeltaZor.cs

**Decision:** Remove all private duplicate methods from `DeltaZor.cs` (see list in §3.1). DeltaZor.cs retains only:
- Public API methods (`CreateDelta`, `ApplyDelta`, `AnalyzeDelta`)
- Public types (`DeltaOptions`, `DeltaStats`, `DeltaResult<T>`, `OpCodeCounts`)
- Necessary `using static` directives for `DeltaUtils`, `DeltaEncoder`, `DeltaDecoder`

The `#region SIMD Helpers` and `#region Private Implementation` sections are deleted entirely.

### 5.8 DefaultOptions Consistency

**Decision:** The `DefaultOptions` in both `DeltaZor.cs` and `DeltaUtils.cs` use `CompressionThreshold = 0.95` (matching the `DeltaOptions` default) instead of the current `2.0` override. The `2.0` value was an undocumented always-prefer-RLE hack.

### 5.9 Zig CRC32 Comptime Table

**Decision:** Convert `crc32` in `utils.zig` to use a `comptime`-computed lookup table:

```zig
const crc32_table = blk: {
    var table: [256]u32 = undefined;
    const poly: u32 = 0xEDB88320;
    for (0..256) |i| {
        var crc: u32 = @intCast(i);
        for (0..8) |_| {
            crc = if ((crc & 1) != 0) ((crc >> 1) ^ poly) else (crc >> 1);
        }
        table[i] = crc;
    }
    break :blk table;
};

pub fn crc32(data: []const u8) u32 {
    var crc: u32 = 0xFFFFFFFF;
    for (data) |b| {
        crc = crc32_table[(crc ^ @as(u32, b)) & 0xFF] ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFF;
}
```

### 5.10 Zig Encoder Double-Write Fix

**Bug:** `emitFromRLEBuffer` writes the opcode in the outer loop (`output[data_pos.*] = entry.opcode; data_pos.* += 1;`) and then again inside the `RLE_MEDIUM_MOTIF` and `RLE_DENSE_MOTIF` cases. This writes the opcode twice, corrupting the output stream.

**Fix:** Remove the duplicate opcode write inside the `RLE_MEDIUM_MOTIF` and `RLE_DENSE_MOTIF` switch cases. The outer loop write is sufficient.

**Note:** After opcode unification (§5.1), these cases are removed entirely, making this fix moot.

### 5.11 Zig Large-Streaming Optimize Fix

**Bug:** The `large_streaming` path in `createRLEDeltaDirect` writes directly to the output buffer and sets `buffer_pos = 0`. After the path-selection block, `optimizeMotifsInBuffer` and `emitFromRLEBuffer` are called unconditionally with `buffer_pos = 0`, performing a no-op.

**Fix:** Guard the optimize+emit calls with `if (!large_streaming)`, or restructure to return early from the function after the large_streaming path.

---

## 6. Cross-Language Interoperability Contract

### 6.1 Byte-for-Byte Equivalence

Given identical `oldData`, `newData`, and `DeltaOptions`, the C# encoder and Zig encoder MUST produce byte-identical delta output. This requires:

1. **Same XOR computation:** `delta[i] = old[i] ^ new[i]` (no SIMD ordering differences).
2. **Same RLE run detection:** Identical run boundaries for zero/non-zero classification.
3. **Same motif detection:** Same unit sizes probed, same savings thresholds, same candidate selection priority.
4. **Same varint encoding:** Identical LEB128 encoding for all counts.
5. **Same CRC32 polynomial and algorithm:** IEEE 802.3 (0xEDB88320 reflected).

### 6.2 Interoperability Test Strategy

- Generate test vectors from C# encoder; verify Zig decoder produces correct output.
- Generate test vectors from Zig encoder; verify C# decoder produces correct output.
- Compare delta bytes from both encoders for identical inputs to verify byte-for-byte equivalence.
- Test edge cases: empty data, single byte, extension only, truncation only, all-zero XOR, all-changed XOR.

### 6.3 Version Compatibility

The wire format version is implicit in the opcode set. Future opcodes (0x06–0x0A) will be additive — decoders encountering unknown opcodes MUST return an error (not silently skip). This ensures forward-incompatible changes are detected immediately.

---

## 7. Encoder Bug Fixes Summary

| # | Component | Bug | Fix |
|---|-----------|-----|-----|
| 1 | C# DeltaZor.cs | Dead private duplicates of Utils methods | Delete all; use `DeltaUtils.*` via `using static` |
| 2 | C# DeltaZor.cs | `DefaultOptions.CompressionThreshold = 2.0` | Change to `0.95` |
| 3 | C# DeltaZor.cs | `ApplyDelta` uses hardcoded `DefaultOptions` for checksum | Read checksum flag from header byte |
| 4 | C# DeltaZor.cs | `unsafe` keyword on non-unsafe private methods | Remove (methods are being deleted) |
| 5 | C# Encoder.cs | `PackChangedPositionsForVarying` was empty | Already implemented — verify correctness |
| 6 | C# Encoder.cs | `EmitOps` Extension case incomplete | Already throws — verify it's unreachable |
| 7 | Zig encoder.zig | Emits 0x80–0xA2 opcodes instead of 0x04/0x05 | Unify to 0x04/0x05 |
| 8 | Zig encoder.zig | `emitFromRLEBuffer` double-writes opcode for MEDIUM/DENSE | Remove duplicate write |
| 9 | Zig encoder.zig | `optimizeMotifsInBuffer` runs on empty buffer for large_streaming | Guard with `if (!large_streaming)` |
| 10 | Zig utils.zig | `crc32` recomputes lookup table every call | Use comptime table |
| 11 | Zig utils.zig | `RLE_UNIFORM_MOTIF_REPEAT = 0xA0` (wrong) | Change to `0x04` |
| 12 | Zig utils.zig | `RLE_VARYING_MOTIF_REPEAT = 0xA1` (wrong) | Change to `0x05` |

---

## 8. Test Fixes Summary

| # | Test | Issue | Fix |
|---|------|-------|-----|
| 1 | ChecksumAndIntegrityTests | Uses default options (checksum disabled) | Create delta with `EnableChecksum = true`; decoder reads flag from header |
| 2 | StrategySelectionTests | Assumes 4-byte checksum always present | Check `EnableChecksum` to determine checksum size |
| 3 | MotifTests assertions | `Assert.False(stats.UsedRLE)` may not hold | Adjust test data or assert output correctness instead |
| 4 | TestGenTests | `ranGenerateTestData = DateTime.Today` always regenerates | Initialize to `DateTime.MinValue` |

---

## 9. Non-Goals

- This architecture does NOT implement opcodes `0x06`–`0x0A` (Float, Half, Channel, Arithmetic, Planar). These remain reserved.
- This architecture does NOT add SIMD vectorization to the Zig encoder. Zig uses scalar loops.
- This architecture does NOT change the public API surface (method signatures, public types) except for checksum self-description.
- This architecture does NOT address `ChannelRun` (0x08) detection logic — it remains planned/unimplemented.
- This architecture does NOT mandate allocator-backed dynamic buffers for the Zig encoder (stack arrays with documented limits are acceptable for now).

---

## 10. File Impact Summary

### C# Files
| File | Changes |
|------|---------|
| `src/csharp/DeltaZor/DeltaZor.cs` | Remove dead private duplicates; remove `DefaultOptions` override; fix `ApplyDelta` to read checksum flag from header; remove `unsafe` keywords |
| `src/csharp/DeltaZor/Encoder.cs` | Verify `PackChangedPositionsForVarying` implementation; verify `EmitOps` throws for Extension/Truncation |
| `src/csharp/DeltaZor/Decoder.cs` | Update to read checksum flag from `compression_type & 0x80` |
| `src/csharp/DeltaZor/Utils.cs` | Fix `DefaultOptions.CompressionThreshold` to `0.95`; add `ChecksumFlag = 0x80` constant |
| `src/csharp/DeltaZorTests/` | Fix checksum tests, strategy tests, motif test assertions, TestGen initialization |

### Zig Files
| File | Changes |
|------|---------|
| `src/zig/src/utils.zig` | Change `RLE_UNIFORM_MOTIF_REPEAT` to `0x04`; change `RLE_VARYING_MOTIF_REPEAT` to `0x05`; add deprecated aliases; comptime CRC32 table; remove/deprecate `0x80`–`0xA2` constants |
| `src/zig/src/encoder.zig` | Unify opcode emission to `0x04`/`0x05`; remove `0x80`–`0xA2` emit paths; fix double-write bug; fix large_streaming optimize guard; update `findMotifCandidate` opcode assignment |
| `src/zig/src/decoder.zig` | Update to read checksum flag from header `& 0x80`; keep `0x80`–`0xA2` decode support for transition; add `0x04`/`0x05` as primary decode path |
| `src/zig/src/deltazor.zig` | No changes needed |
