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
- 📋 **Float Pattern Detection (Planned)**: Cache-line efficient float detection is designed but not implemented in the core library — `FloatPatternCount` is reserved in `OpCodeCounts` (`DeltaZor.cs`, marked `// 0x06 (Planned)`) and always reads 0; no float/half opcode is emitted or decoded
- ✅ **Platform Stability**: Resolved ARM/x64 execution issues
- ✅ **Pattern Counts Feature**: Added opcode tracking for diagnostic information (the `ChannelRunCount` slot is reserved for the planned channel opcode and always reads 0 today)
- ✅ **Motif Detection Refinement**: Completed lazy, single-accumulator motif detection with bit-packed SIMD vectors for variable UnitSizes 2-8, enabling single-pass variable-length probing at opcode boundaries with allocation-free stack operations and O(1) amortized cost per opcode.

## Current Focus
- 🔄 **Float Data Testing**: Preparing height map examples with float32 and float16 data, including motif integration for repeating patterns in scientific tensors
- 🔄 **Advanced Compression Modes**: Planning implementation of arithmetic compression features, with motif nesting for hybrids
- 🔄 **Tensor File Support**: Investigating format-specific optimizations for ML/Scientific data

---

# Feature Roadmap with Dual Implementation

| # | Feature | Why It Matters | C# Implementation | Zig Implementation |
|---|--------|----------------|-------------------|--------------------|
| 1 | **RLE+XOR Delta (Core)** | Foundation for sparse changes | ✅ Complete (`DeltaZor.cs`) | `rle_xor.zig` |
| 2 | **SIMD Acceleration** | 4–8× faster on large buffers | ✅ Complete (`Vector128`) | `std.simd`, `@Vector` |
| 3 | **Zero-Allocation API** | No GC in hot paths | ✅ Complete (`ReadOnlySpan<byte>`) | `[]const u8`, `[]u8` |
| 4 | **7-bit Varint** | Compact run lengths | ✅ Complete (`Write7BitEncodedInt`) | `writeVarint`, `readVarint` |
| 5 | **XxHash32 Checksum** | Corruption detection | ✅ Complete (`XxHash32Wrapper.Compute` → `System.IO.Hashing.XxHash32`) | `std.hash.XxHash32` (`utils.zig`) |
| 6 | **Hybrid Strategy** | Always optimal size | ✅ Fixed (`ShouldUseRLE` threshold) | `estimate_rle_size` |
| 7 | **Full Replace** | Safe fallback | ✅ Complete (`CompressionType_FullReplace`) | `TYPE_FULL` |
| 8 | **Unified Header** | Interop, versioning | ✅ Complete (`[len:4][type:1][data][checksum:4]`) | Same binary layout |
| 9 | **Channel Pattern Detection** | ... | 📋 Planned (`RLE_ChannelRun = 0x08` opcode + `ChannelPattern` struct reserved in `Utils.cs`; not emitted or decoded — `AnalyzeChannelPattern` absent, `OpCodeCounts.ChannelRunCount` marked `// (Planned)`) | Planned |
| 9.1 | **Motif Repeat Detection (Refined)** | Sparse repeating patterns with variable UnitSizes 2-8 via lazy single-accumulator | ✅ Complete: Single MotifAccumulator with Vector256-packed rolling masks/hashes, lazy updates at opcode boundaries for allocation-free, SIMD-accelerated detection | `motif_accum.zig`: @Vector-packed state with inline bit ops for single-pass probing |
| 10 | **Pattern Counts** | ... | ✅ Complete (`PatternCounts`) | `pattern_counts.zig` |

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

## 5. XxHash32 Checksum

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Detect bit flips | `System.IO.Hashing.XxHash32.HashToUInt32` | `std.hash.XxHash32.hash` |
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

## 9. Channel Pattern Detection (Planned)

> **Status:** Planned / not implemented. The `RLE_ChannelRun = 0x08` opcode constant and the `ChannelPattern` struct are reserved in `Utils.cs`, but the channel opcode is **never emitted or decoded**: there is no `AnalyzeChannelPattern` method, the encoder/decoder do not reference `RLE_ChannelRun`, and `OpCodeCounts.ChannelRunCount` is marked `// (Planned)` in `DeltaZor.cs`. The design below is the intended approach; the code samples are illustrative of the planned shape, not the current implementation.

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
- **Tensor Support**: Perfect for ML tensor data with feature channels; integrates with motif repeats for masked hybrids via flags

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
    public int FloatPatternCount { get; init; }   // Future: 0x06
    public int HalfPatternCount { get; init; }    // Future: 0x07
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
| 11 | **Global Arithmetic** | `+5` on 1M ints → 8 B | 📋 Planned (`RLE_Arithmetic = 0x09` constant reserved; not wired into encode/decode; `ArithmeticCompressionTests` all `[Skip]`) | Planned |
| 12 | **Planar Arithmetic** | `R+10` on 1080p → 20 B | 📋 Planned (`RLE_Planar = 0x0A` constant reserved; not wired; tests `[Skip]`) | Planned |
| 13 | **Per-Run Arithmetic** | Fill tool → 30 B; nestable with motifs for sparse runs | Planned (integrate with MotifAccumulator for hybrid detection) | `try_run_arithmetic` (with motif_accum hooks) |
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

# Benchmarks (Projected Targets)

> **Status legend:** ✅ = achievable today by the implemented core (RLE+XOR / MOTIF);
> 📋 = requires a *planned* compression mode (arithmetic / channel) and is a projected
> design target, **not implemented**. All byte sizes are illustrative design targets — there
> is no automated benchmark harness yet; the only executed cross-language suite is the 43-case
> shared parity test-vector set.

| Test | Data | C# | Zig |
|------|------|----|-----|
| `Sparse_1KB` | 1% changed | ✅ <50 B | <50 B |
| `Uniform_Int_1M` | +5 (arithmetic) | 📋 8 B (planned) | 📋 8 B (planned) |
| `Color_Fill_200x200` | Fill tool | ✅ ~30 B | ~30 B |
| `1080p_Tint` | R+10 (channel/arithmetic) | 📋 20 B (planned) | 📋 20 B (planned) |
| `RGBA_AlphaOnly` | Alpha channel edit (channel) | 📋 ~25% of original (planned) | 📋 ~25% (planned) |

---

# Milestones

| Milestone | Tasks | ETA |
|---------|-------|-----|
| **v0.1** | C# Core (RLE+XOR, SIMD) | ✅ Complete |
| **v0.2** | Zig Port (RLE+XOR) | +1 week |
| **v0.3** | Motif refinement with lazy single-accumulator detection (Arithmetic Global + Planar deferred) | 🔄 Motif refinement ✅ done; Arithmetic 📋 planned |
| **v0.4** | RunArithmetic + Clamp | 📋 Planned |
| **v0.5** | Pattern Counts ✅ done; Channel Runs 📋 planned (opcode reserved, not wired) | 🔄 Partial |
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

- October 28, 2025: Updated roadmap for chunk-vetoed, mask-based motifs; emphasized SIMD and allocation-free refinements; incorporated lazy single-accumulator detection for variable UnitSizes 2-8 with single-pass, opcode-bound updates.