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

# Feature Roadmap with Dual Implementation

| # | Feature | Why It Matters | C# Implementation | Zig Implementation |
|---|--------|----------------|-------------------|--------------------|
| 1 | **RLE+XOR Delta (Core)** | Foundation for sparse changes | `OptimizedDelta.cs` → `DeltaZor.Core` | `rle_xor.zig` |
| 2 | **SIMD Acceleration** | 4–8× faster on large buffers | `Vector128`, `Vector256` | `std.simd`, `@Vector` |
| 3 | **Zero-Allocation API** | No GC in hot paths | `ReadOnlySpan<byte>` | `[]const u8`, `[]u8` |
| 4 | **7-bit Varint** | Compact run lengths | `Write7BitEncodedInt` | `writeVarint`, `readVarint` |
| 5 | **CRC32 Checksum** | Corruption detection | `Crc32.Compute` | `crc32.zig` (lookup table) |
| 6 | **Hybrid Strategy** | Always optimal size | `ShouldUseRLE` | `estimate_rle_size` |
| 7 | **Full Replace** | Safe fallback | `CompressionType_FullReplace` | `TYPE_FULL` |
| 8 | **Unified Header** | Interop, versioning | `[len:4][type:1][data][crc:4]` | Same binary layout |

---

## 1. RLE+XOR Delta (Core)

| **Why** | **C#** | **Zig** |
|-------|--------|--------|
| Sparse edits (UI, game state) | `CreateRLEDelta()` | `rle_xor_encode()` |
| Length changes | `RLE_Extension`, `RLE_Truncation` | `OP_EXTEND`, `OP_TRUNCATE` |
| **Status** | Complete | Port from C# logic |

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
| Pick smallest | `EstimateRLESize` | `estimate_rle_size` |
| Threshold | `CompressionThreshold` | `threshold: f32` |

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

# Advanced Features (Dual)

| # | Feature | Why It Matters | C# | Zig |
|---|--------|----------------|----|-----|
| 9 | **Global Arithmetic** | `+5` on 1M ints → 8 B | `TryDetectArithmetic<T>` | `detect_global_shift` |
|10| **Planar Arithmetic** | `R+10` on 1080p → 20 B | Channel split | `planar_detect` |
|11| **Per-Run Arithmetic** | Fill tool → 30 B | Inline in RLE | `try_run_arithmetic` |
|12| **RunArithmetic Opcode** | Local uniform edits | `0x04` | `OP_ARITH_RUN` |
|13| **Clamp-Aware** | `255+10=255` | `Math.Clamp` | `clamp_u8` |
|14| **Auto-Mode** | Best of all | Try all | `select_best_mode` |

---

## 9. Global Arithmetic Shift

| **C#** | **Zig** |
|-------|--------|
| `MemoryMarshal.Cast<T>` | `@ptrCast(*T, data)` |
| `Vector128.EqualsAll` | `@Vector(4, T)` comparison |
| Early exit | `break` on mismatch |

---

## 10. Planar Arithmetic (Color)

| **C#** | **Zig** |
|-------|--------|
| `oldData[0::4]` slicing | `data[i*4]` stride |
| Per-channel diff | `diff_r`, `diff_g`, `diff_b` |
| `TYPE_PLANAR_ARITH` | `TYPE_PLANAR` |

---

## 11. Per-Run Arithmetic

| **C#** | **Zig** |
|-------|--------|
| Inline in RLE loop | `while (changed) { ... }` |
| `TryEncodeRunArithmetic` | `if (try_arithmetic_run(...))` |
| **Cost**: +5% | **Cost**: +0.1 ms |

---

## 12. RunArithmetic Opcode

| **C#** | **Zig** |
|-------|--------|
| `RLE_RunArithmetic = 0x04` | `const OP_ARITH_RUN = 0x04;` |
| `[type:1][value:4]` | `writeU8(type); writeI32(value);` |

---

## 13. Clamp-Aware Detection

| **C#** | **Zig** |
|-------|--------|
| `Math.Clamp(a + d, 0, 255)` | `std.math.clamp(a +% d, 0, 255)` |
| Only for `byte` runs | `if (T == u8)` |

---

## 14. Auto-Mode Selection

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
};
```

---

# API Surface

| **C#** | **Zig** |
|-------|--------|
| `DeltaZor.Create(old, new)` | `delta_zor_create(old, new, &out)` |
| `TryCreate(..., out written)` | `delta_zor_try_create(..., &written)` |
| `Apply(old, delta, out)` | `delta_zor_apply(old, delta, out)` |

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
| `Sparse_1KB` | 1% changed | <50 B | <50 B |
| `Uniform_Int_1M` | +5 | 8 B | 8 B |
| `Color_Fill_200x200` | Fill tool | ~30 B | ~30 B |
| `1080p_Tint` | R+10 | 20 B | 20 B |

---

# Milestones

| Milestone | Tasks | ETA |
|---------|-------|-----|
| **v0.1** | C# Core (RLE+XOR, SIMD) | Done |
| **v0.2** | Zig Port (RLE+XOR) | +1 week |
| **v0.3** | Arithmetic (Global + Planar) | +1 week |
| **v0.4** | RunArithmetic + Clamp | +1 week |
| **v0.5** | Auto-Mode + Interop Tests | +1 week |
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


