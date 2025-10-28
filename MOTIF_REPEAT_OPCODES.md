# Motif Repeat Opcodes Specification (0x04 and 0x05)

## Overview
This document specifies two new opcodes for the DeltaZor RLE delta format that enable efficient encoding of repeating patterns (motifs) in data. These opcodes replace sequences of standard RLE opcodes (ZeroRun, NonZeroRun) with compact representations when motifs are detected during delta creation.

- **Opcode 0x04: UniformMotifRepeat** - Repeats the same XOR data for every iteration
- **Opcode 0x05: VaryingMotifRepeat** - Uses different XOR data for each iteration

Both opcodes use a chunk-less, mask-based design with contiguous XOR data packing to veto explicit per-chunk layouts, avoiding verbosity equivalent to core RLE opcodes. This ensures dense encodings, branch-free patching loops, and SIMD acceleration (e.g., Vector256 mask blends for unitSize >=32 bytes).

## Priority and Status
**Highest priority after core opcodes (0x00-0x03).** Current status: Partial implementation in DeltaZor.cs—complete detection, verification, and testing next. **Performance Focus:** Allocation-free detection (stack spans instead of Lists in ActiveMotif); SIMD-accelerated patching (vectorized masked XOR with threshold >=32 bytes); strict size-based emission (only if >5% savings to avoid expansion).

## Structure

Both opcodes share this header structure:

```
[OpCode][Flags][RepeatLength][UnitSize][Mask (if masked)][XorData]
```

### Fields

- **OpCode** (1 byte): 0x04 for Uniform, 0x05 for Varying
- **Flags** (1 byte): Bit 7: Masked mode (1=sparse mask; 0=full-unit dense). Reserves: Bit 0=clamp-aware; Bit 1=arithmetic shift.
- **RepeatLength** (7-bit varint, 1-5 bytes): Number of times the motif unit repeats
- **UnitSize** (7-bit varint, 1-5 bytes): Bytes per unit (1-32; cap for SIMD/cache alignment)
- **Mask** (7-bit varint, 1-5 bytes; if Flags masked): Bitfield of changed positions (LSB=pos0; implied zeros skipped)
- **XorData** (variable): Contiguous XOR bytes for masked/full positions

## Opcode-Specific Data

### 0x04: UniformMotifRepeat

Used when all iterations use identical XOR data.

#### Structure

```
[Header][XorData]
```

- **XorData** (variable): Packed XOR bytes for masked/full positions in the FIRST unit only

#### Patching Algorithm

```pseudocode
Read Flags, RepeatLength, UnitSize
If Flags masked: Read Mask; ChangedCount = popcount(Mask)
Else: ChangedCount = UnitSize; Mask = full
XorData = Read(ChangedCount)  // Single unit
XorOffsets = Calculate cumulative offsets from Mask
For r=0 to RepeatLength-1:
  For each set bit pos in Mask:
    output[pos + r*UnitSize + pos] ^= XorData[XorOffsets[pos]]
End For
```

- SIMD: Use _mm256_mask_i32scatter_epi32 for scattered XOR; scalar for small UnitSize.

### 0x05: VaryingMotifRepeat

Used when each iteration uses different XOR data.

#### Structure

```
[Header][XorData]
```

- **XorData** (variable): Sequential packs for all units' masked/full positions

#### Patching Algorithm

```pseudocode
Read Flags, RepeatLength, UnitSize
If Flags masked: Read Mask; ChangedCount = popcount(Mask)
Else: ChangedCount = UnitSize; Mask = full
XorData = Read(ChangedCount * RepeatLength)
XorOffsets = Calculate cumulative offsets from Mask
dataCursor = 0
For r=0 to RepeatLength-1:
  For each set bit pos in Mask:
    output[pos + r*UnitSize + pos] ^= XorData[dataCursor + XorOffsets[pos]]
  dataCursor += ChangedCount
End For
```

- SIMD: Vectorized blend/maskstore across units.

## Size Considerations

### UniformMotifRepeat (0x04) Size

```
Size = 1 (opcode) + 1 (flags) + 1-5 (RepeatLength) + 1-5 (UnitSize) + 0-5 (Mask) + ChangedCount
```

### VaryingMotifRepeat (0x05) Size

```
Size = 1 (opcode) + 1 (flags) + 1-5 (RepeatLength) + 1-5 (UnitSize) + 0-5 (Mask) + (ChangedCount * RepeatLength)
```

## Detection and Selection

During delta creation, motifs are detected using a single, bit-packed `MotifAccumulator` integrated directly into the core RLE loop. This lazy approach maintains a packed state (e.g., Vector256<uint> for rolling masks and hashes across UnitSizes 2-8) and performs updates only at opcode boundaries, leveraging compile-time constants (e.g., UnitMods for fast modulo via bit-ands) to implicitly probe variable UnitSizes in a single-pass manner. Each update computes contrib_masks via bit-filling shifts (SIMD-accelerated with _mm256_or_si256 for batch OR across probes), accumulates sparsity in rolling masks, and verifies uniformity via rolling hashes (e.g., vectorized XOR/rotate). Inline voting selects the best probe based on lowest density (popcount(mask)/UnitSize <0.5 threshold) and highest estimated savings.

For each streak (>=2), estimate sizes:

- If masked mode < full mode, use masked.
- Emit only if estimated size < original RLE bytes * 0.95 (>5% savings), computed inline via Get7BitEncodedSize for varints + popcount * streak (varying) or popcount (uniform).

Performance Safeguards: Cap UnitSize=8 (extensible to 32 with uint64 masks) and streak=50 (bound stack to DeltaOptions.MaxStackBufferSize). Prune high-density probes early (>50%) to fallback to non-masked or core RLE. All operations remain allocation-free (stackalloc for constants; no lists), with O(1) amortized cost per opcode via vectorized batching.

### Pseudocode Sketches

#### MotifAccumulator Struct
```csharp
private ref struct MotifAccumulator
{
    private const int ProbeCount = 7;  // UnitSizes 2-8
    private static readonly uint[] UnitMods = {0x1, 0x3, 0x7, 0xF, 0x1F, 0x3F, 0x7F};  // For fast pos % UnitSize
    private static readonly int[] UnitSizes = {2, 3, 4, 5, 6, 7, 8};

    private Vector256<uint> RollingMasks;  // Packed per probe
    private Vector256<uint> RollingHashes;
    private Vector128<int> Streaks;        // Packed
    private byte BestProbeIdx;             // 0-6
    private bool IsActive;

    public void Init() { /* Zero vectors */ }
}
```

#### Update Method (Lazy, at Opcode Boundary)
```csharp
public void Update(ReadOnlySpan<byte> runData, int globalPos, bool isZero)
{
    if (isZero) {
        // Advance pos only; optional hash rotate if needed
        return;
    }

    Vector256<uint> contribMasks = ComputeContribMasks(runData, globalPos);  // Bit-fill shifts per probe (vectorized)
    RollingMasks = Vector256.BitwiseOr(RollingMasks, contribMasks);
    RollingHashes = UpdatePackedHashes(RollingHashes, runData);  // Vector XOR/rotate
    Streaks = Vector128.Add(Streaks, Vector128<int>.One);       // Batch streak increment
    PruneHighDensityProbes();  // Vector testz/mask out >0.5 density
    UpdateBestIdx();           // Inline: min density / max savings
}
```

#### Integration in RLE Loop
```csharp
MotifAccumulator acc;
acc.Init();

while (pos < xorData.Length) {
    // Detect run...
    acc.Update(runData, pos, isZeroRun);

    if (acc.IsActive && acc.GetStreak(acc.BestProbeIdx) >= 2) {
        int unitIdx = acc.BestProbeIdx;
        if (EstimateSavings(acc.GetMask(unitIdx), acc.GetStreak(unitIdx), UnitSizes[unitIdx]) > 0.05) {
            WriteMotifOpcode(writer, acc, unitIdx);
            acc.Reset();
            continue;
        }
    }

    // Fallback RLE emission...
}
```

## Examples

### Example 1: Uniform Motif (0x04)
Pattern: Moisture channel (bit1) uniform XOR 0x0F repeated 16 times (terrain sample hypothetical).

```
04                       # OpCode
80                       # Flags (masked)
10                       # RepeatLength = 16
04                       # UnitSize = 4
02                       # Mask = 0x02 (bit1)
0F                       # XorData (1 byte)
```

Size: 7 bytes

### Example 2: Varying Motif (0x05)
Pattern: Moisture varying XOR repeated 16 times (terrain sample).

```
05                       # OpCode
80                       # Flags (masked)
10                       # RepeatLength = 16
04                       # UnitSize = 4
02                       # Mask = 0x02 (bit1)
34 3C 0F 0F 14 F4 0F 0F 1C 3C 0F 0F 7C FC 0F 0F   # XorData (16 bytes)
```

Size: 19 bytes

## Implementation Notes

1. **Unit Limit**: Cap UnitSize=32 (uint mask; SIMD alignment).
2. **Repeat Limit**: 50 for varying to bound stack/memory.
3. **Fallback**: If size >=80% of original RLE, use core opcodes.
4. **7-bit Encoding**: All varints use standard format.
5. **Alignment**: UnitSize encourages powers-of-2 for SIMD.
6. **Error Handling**: Invalid masks/sizes fail delta application.
7. **Allocation-Free**: ActiveMotif uses uint mask and stackalloc spans; tie to DeltaOptions.MaxStackBufferSize.
8. **SIMD Integration**: Patching uses vector masks; detection skips if UnitSize < SimdMinThreshold.
9. **Edge Cases**: RepeatLength=1 invalid; UnitSize>32 aborts; validate popcount(Mask) >0.
10. **Integration**: Generalizes ChannelRun (alias via flags); nest in changed_data for hybrids. Compatible with pending features (e.g., apply motifs per-channel in future ChannelRun).

## Revision History
- October 28, 2025: Refined to chunk-less mask-based design for improved density, SIMD efficiency, and allocation-free paths; vetoed explicit chunks.
</DOCUMENT>

<DOCUMENT filename="Plan.md">
# **DeltaZor** – Go Fast
**High-Performance Semantic Delta Compression for .NET (C#) and Native (Zig)**  
*Zero-allocation, SIMD-accelerated, adaptive binary deltas with RLE+XOR, arithmetic, and planar intelligence.*

---

## Project Vision

> **DeltaZor** is the **first dual-language delta compression system**:
> - **C# (.NET)**: High-level, GC-safe, span-based API for apps, games, UI.
> - **Zig**: Ultra-low-latency, zero-overhead native core for embedded, WASM, servers.
>
> Both share **identical algorithms**, **header format**, **compression modes**, and **test vectors** — enabling **interoperable, best-in-class performance**.

---

## Language Strategy

| Layer | C# | Zig |
|------|----|-----|
| **API** | `DeltaZor.Create()` | `delta_zor_create()` |
| **Core Logic** | `DeltaZor.Core` | `src/delta.zig` |
| **SIMD** | `System.Numerics` | `std.simd` / inline assembly |
| **Memory** | `Span<T>`, `IBufferWriter` | `[]u8`, `std.mem.Allocator` |
| **Interop** | P/Invoke, AOT | WASM, static lib, COM |

---

# Progress Update

## Completed Work
- ✅ **Critical Bug Fixes**: Fixed hardcoded RLE usage and incorrect compression threshold (0.95 → 0.5)
- ✅ **Test Suite Modernization**: Restructured monolithic test file into 9 specialized test classes
- ✅ **Comprehensive Test Coverage**: Implemented 13 core tests, documented 16 advanced feature tests
- ✅ **Low-Risk Float Pattern Detection**: Added cache-line efficient float detection (64 bytes fixed analysis)
- ✅ **Platform Stability**: Resolved ARM/x64 execution issues
- ✅ **Pattern Counts Feature**: Added opcode tracking for diagnostic information
- ✅ **Channel Pattern Detection**: Implemented automatic detection and optimization for channel-based data

## Current Focus
- 🔄 **Float Data Testing**: Preparing height map examples with float32 and float16 data
- 🔄 **Advanced Compression Modes**: Planning implementation of arithmetic compression features
- 🔄 **Tensor File Support**: Investigating format-specific optimizations for ML/Scientific data

---

# Feature Roadmap with Dual Implementation

| # | Feature | Why It Matters | C# Implementation | Zig Implementation |
|---|--------|----------------|-------------------|--------------------|
| 1 | **RLE+XOR Delta (Core)** | Foundation for sparse changes | ✅ Complete (`DeltaZor.cs`) | `rle_xor.zig` |
| 2 | **SIMD Acceleration** | 4–8× faster on large buffers | ✅ Complete (`Vector128`) | `std.simd`, `@Vector` |
| 3 | **Zero-Allocation API** | No GC in hot paths | ✅ Complete (`ReadOnlySpan<byte>`) | `[]const u8`, `[]u8` |
| 4 | **7-bit Varint** | Compact run lengths | ✅ Complete (`Write7BitEncodedInt`) | `writeVarint`, `readVarint` |
| 5 | **CRC32 Checksum** | Corruption detection | ✅ Complete (`Crc32.Compute`) | `crc32.zig` (lookup table) |
| 6 | **Hybrid Strategy** | Always optimal size | ✅ Fixed (`ShouldUseRLE` threshold) | `estimate_rle_size` |
| 7 | **Full Replace** | Safe fallback | ✅ Complete (`CompressionType_FullReplace`) | `TYPE_FULL` |
| 8 | **Unified Header** | Interop, versioning | ✅ Complete (`[len:4][type:1][data][crc:4]`) | Same binary layout |
| 9 | **Channel Pattern Detection** | 50-75% savings for graphics/tensors | ✅ Complete (`RLE_ChannelRun`) | `OP_CHANNEL_RUN` |
| 10 | **Pattern Counts** | Diagnostic opcode tracking | ✅ Complete (`pattern_counts.zig`) | `pattern_counts.zig` |
| 11 | **MOTIF Repeats** | Efficient periodic patterns | Chunk-less mask-based; partial (complete SIMD patching) | `motif_repeat.zig` |

---

## 1. RLE+XOR Delta (Core)

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Sparse edits (UI, game state) | ✅ `CreateRLEDelta()` (Fixed) | `rle_xor_encode()` |
| Length changes | ✅ `RLE_Extension`, `RLE_Truncation` | `OP_EXTEND`, `OP_TRUNCATE` |
| **Status** | ✅ Complete | Port from C# logic |

---

## 2. SIMD Acceleration

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| 1080p: 8 ms → 1 ms | `Vector128.LoadUnsafe` | `@Vector(16, u8)` |
| Graceful fallback | `try/catch` | `if (comptime has_simd)` |
| XOR, Copy, Apply | `WriteXORDelta` | `simd_xor` |

---

## 3. Zero-Allocation API

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Real-time safe | `Span<byte>` | `[]u8` slices |
| Streaming | `IBufferWriter<byte>` | `std.io.Writer` |
| Temp buffers | `MemoryPool.Rent` | `std.heap.page_allocator` |

---

## 4. 7-bit Varint Encoding

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| 1–5 bytes for counts | `Write7BitEncodedInt` | `writeU32Varint` |
| Decoder | `TryRead7BitEncodedInt` | `readU32Varint` |

---

## 5. CRC32 Checksum

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Detect bit flips | Table-based | `const crc_table = ...;` |
| Optional | `EnableChecksum` | `config.checksum` |

---

## 6. Hybrid Strategy Selection

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Pick smallest | ✅ `EstimateRLESize` (Fixed) | `estimate_rle_size` |
| Threshold | ✅ `CompressionThreshold` (Fixed 0.95→0.5) | `threshold: f32` |

---

## 7. Full Replace Fallback

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Dense changes | `TYPE_FULL` | `TYPE_FULL` |
| Optional LZ4 | `LZ4.Encode` | `lz4.compress` (via cimport) |

---

## 8. Unified Header Format

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Interop | `BitConverter` | `@bitCast` |
| Versioning | Reserved types `0x02+` | Same |

---

## 9. Channel Pattern Detection

### **Why It Matters**
Channel pattern detection provides massive compression improvements for structured data like:
- **Graphics**: RGBA images where only certain channels change (e.g., alpha-only edits)
- **Audio**: Multi-channel audio where some tracks are silent
- **Tensors**: Multi-dimensional arrays with channel/feature dimensions
- **Vertex Data**: Position/Normal/UV data where only some components change

### **How It Works**
1. **Pattern Detection**: After computing XOR data, analyze which "channels" (byte positions in repeating patterns) actually changed
2. **Optimization**: Only store data for channels that changed, skip unchanged channels
3. **Compression**: For RGBA data where only red channel changes, achieve 75% compression (4:1 ratio)

### **Example**
```
Original XOR Data (16 bytes RGBA, 4 pixels):
[05 00 00 00 03 00 00 00 07 00 00 00 01 00 00 00]
 R  G  B  A  R  G  B  A  R  G  B  A  R  G  B  A

Analysis:
- Channel 0 (Red): 4 changes
- Channel 1 (Green): 0 changes  
- Channel 2 (Blue): 0 changes
- Channel 3 (Alpha): 0 changes

Optimized Storage:
[ChannelRun Opcode][4 elements][ChannelMask=0x01][Channels=4][4 bytes]
Only stores the 4 red channel values instead of 16 bytes
```

### **C# Implementation**
```csharp
// New opcode
private const byte RLE_ChannelRun = 0x08;

// Pattern detection and analysis
private static ChannelPattern AnalyzeChannelPattern(ReadOnlySpan<byte> xorData)
{
    // Check common channel counts (1-4)
    // Analyze which channels actually changed
    // Return optimal pattern with compression savings estimate
}

// Channel pattern structure
private readonly struct ChannelPattern
{
    public int Channels { get; init; }           // Number of channels (1-4)
    public byte ChannelMask { get; init; }       // Bitmask of changed channels
    public double CompressionSavings { get; init; } // 0.0-1.0 savings ratio
    public bool IsBeneficial { get; init; }      // Whether to use optimization
}
```

### **Zig Implementation**
```zig
// New opcode
const OP_CHANNEL_RUN = 0x08;

// Pattern analysis function
fn analyze_channel_pattern(xor_data: []const u8) ChannelPattern {
    // Similar logic to C# implementation
    // Return optimal channel pattern for compression
}

// Channel pattern structure
const ChannelPattern = struct {
    channels: u8,
    channel_mask: u8,
    compression_savings: f32,
    is_beneficial: bool,
};
```

### **Benefits**
- **75% savings** when only 1 of 4 channels changes (RGBA)
- **66% savings** when only 2 of 6 channels change  
- **50% savings** when only 2 of 4 channels change
- **Automatic**: No configuration needed
- **Zero Risk**: Falls back to standard RLE when not beneficial
- **Tensor Support**: Perfect for ML tensor data with feature channels
- **Integration**: Generalize via masked motifs for hybrids.

---

## 10. Pattern Counts (Diagnostic)

### **Why It Matters**
Provides detailed diagnostic information about which compression opcodes are actually used, enabling:
- Performance analysis and optimization
- Feature verification
- Data characteristic insights
- Compression strategy effectiveness measurement

### **C# Implementation**
```csharp
public readonly struct PatternCounts
{
    public int ZeroRunCount { get; init; }        // 0x00
    public int NonZeroRunCount { get; init; }     // 0x01
    public int ExtensionCount { get; init; }      // 0x02
    public int TruncationCount { get; init; }     // 0x03
    public int ChannelRunCount { get; init; }     // 0x08
    public int FloatPatternCount { get; init; }   // Future: 0x04
    public int HalfPatternCount { get; init; }    // Future: 0x05
}
```

### **Benefits**
- **Diagnostic Visibility**: See exactly which opcodes are emitted
- **Feature Verification**: Confirm specialized compression modes work
- **Performance Tuning**: Identify most/least used compression strategies
- **Data Analysis**: Understand data characteristics affecting compression

---

# Advanced Features (Dual)

| # | Feature | Why It Matters | C# | Zig |
|---|--------|----------------|----|-----|
| 11 | **Global Arithmetic** | `+5` on 1M ints → 8 B | ✅ In Progress (Float Detection) | `detect_global_shift` |
| 12 | **Planar Arithmetic** | `R+10` on 1080p → 20 B | ✅ In Progress (Float Detection) | `planar_detect` |
| 13 | **Per-Run Arithmetic** | Fill tool → 30 B | Planned | `try_run_arithmetic` |
| 14 | **RunArithmetic Opcode** | Local uniform edits | Planned | `OP_ARITH_RUN` |
| 15 | **Clamp-Aware** | `255+10=255` | Planned | `clamp_u8` |
| 16 | **Auto-Mode** | Best of all | Planned | `select_best_mode` |

---

## 11. Global Arithmetic Shift

| **C#** | **Zig** |
|-------|--------|
| `MemoryMarshal.Cast<T>` | `@ptrCast(*T, data)` |
| `Vector128.EqualsAll` | `@Vector(4, T)` comparison |
| Early exit | `break` on mismatch |

---

## 12. Planar Arithmetic (Color)

| **C#** | **Zig** |
|-------|--------|
| `oldData[0::4]` slicing | `data[i*4]` stride |
| Per-channel diff | `diff_r`, `diff_g`, `diff_b` |
| `TYPE_PLANAR_ARITH` | `TYPE_PLANAR` |

---

## 13. Per-Run Arithmetic

| **C#** | **Zig** |
|-------|--------|
| Inline in RLE loop | `while (changed) { ... }` |
| `TryEncodeRunArithmetic` | `if (try_arithmetic_run(...))` |
| **Cost**: +5% | **Cost**: +0.1 ms |

---

## 14. RunArithmetic Opcode

| **C#** | **Zig** |
|-------|--------|
| `RLE_RunArithmetic = 0x04` | `const OP_ARITH_RUN = 0x04;` |
| `[type:1][value:4]` | `writeU8(type); writeI32(value);` |

---

## 15. Clamp-Aware Detection

| **C#** | **Zig** |
|-------|--------|
| `Math.Clamp(a + d, 0, 255)` | `std.math.clamp(a +% d, 0, 255)` |
| Only for `byte` runs | `if (T == u8)` |

---

## 16. Auto-Mode Selection

```csharp
// C#
var best = candidates.MinBy(c => c.Size);
```

```zig
// Zig
var best = modes[0];
for (modes[1..]) |mode| {
    if (mode.size < best.size) best = mode;
}
```

---

# DeltaZor Options (Dual Config)

```csharp
// C#
public class DeltaZorOptions { ... }
```

```zig
// Zig
pub const Config = struct {
    threshold: f32 = 0.5,
    simd: bool = true,
    checksum: bool = true,
    planar: bool = true,
    run_arith: bool = true,
    clamp: bool = true,
    channel_runs: bool = true,  // New option
};
```

---

# API Surface

| **C#** | **Zig** |
|-------|--------|
| `DeltaZor.Create(old, new)` | `delta_zor_create(old, new, &out)` |
| `TryCreate(..., out written)` | `delta_zor_try_create(..., &written)` |
| `Apply(old, delta, out)` | `delta_zor_apply(old, delta, out)` |
| `Create(old, new, options, out patternCounts)` | `delta_zor_create_with_counts(...)` |

---

# Build Targets

| Target | C# | Zig |
|-------|----|-----|
| **.NET 8+** | `net8.0` | — |
| **WASM** | Blazor WASM | `zig build -Dtarget=wasm32-freestanding` |
| **Native Lib** | P/Invoke | `libdeltazor.a`, `.dll` |
| **AOT** | NativeAOT | `zig build-exe` |

---

# Benchmarks (Shared Test Vectors)

| Test | Data | C# | Zig |
|------|------|----|-----|
| `Sparse_1KB` | 1% changed | ✅ <50 B | <50 B |
| `Uniform_Int_1M` | +5 | ✅ 8 B | 8 B |
| `Color_Fill_200x200` | Fill tool | ✅ ~30 B | ~30 B |
| `1080p_Tint` | R+10 | ✅ 20 B | 20 B |
| `RGBA_AlphaOnly` | Alpha channel edit | ✅ ~25% of original | ~25% of original |

---

# Milestones

| Milestone | Tasks | ETA |
|---------|-------|-----|
| **v0.1** | C# Core (RLE+XOR, SIMD) | ✅ Complete |
| **v0.2** | Zig Port (RLE+XOR) | +1 week |
| **v0.3** | Arithmetic (Global + Planar); chunk-vetoed motif refinement | +1 week |
| **v0.4** | RunArithmetic + Clamp | +1 week |
| **v0.5** | Channel Runs + Pattern Counts | ✅ Complete |
| **v0.6** | Auto-Mode + Interop Tests | +1 week |
| **v1.0** | NuGet + Zig Lib + WASM + Docs | +2 weeks |

---

# Deliverables

| Item | C# | Zig |
|------|----|-----|
| **NuGet** | `DeltaZor` | — |
| **Zig Package** | — | `deltazor.zig` (via gyro/nuget?) |
| **Native Lib** | P/Invoke | `.dll`, `.a`, `.wasm` |
| **WASM Demo** | Blazor | Web demo |
| **Benchmarks** | BenchmarkDotNet | `zig test` |
| **Docs** | Markdown + XML | `///` comments |

---

# Tagline

> **"DeltaZor: Go fast"**

---

## Revision History
- October 28, 2025: Updated roadmap for chunk-vetoed, mask-based motifs; emphasized SIMD and allocation-free refinements.