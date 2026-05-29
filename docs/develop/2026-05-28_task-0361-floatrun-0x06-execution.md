# TASK-0361 — FloatRun 0x06 opcode (C# + Zig, byte-parity) — execution log

- **Date:** 2026-05-28
- **Branch:** `task-0361-floatrun-0x06`
- **Impl-kind:** claude (opus, no-sandbox dev lane)
- **Epic:** EPIC-0045 (Float/Half/Channel pattern detection 0x06–0x08)
- **Base:** master `a0611ec` (EPIC-0044 byte-parity, 45 create-delta vectors)

## 1. Design (written BEFORE implementing)

### 1.1 Motivation / gap analysis

The RLE-delta stream (compression type 0x00) is a sequence of inner opcodes operating on
the **byte-level XOR stream**. The existing motif opcodes (0x04 Uniform, 0x05 Varying)
capture *repeating units* but the live `MotifAccumulator` probes only unit sizes **2–8**
(`motif_max_unit_size = 8`). Two real float-delta shapes therefore fall through to the
basic Zero/NonZero byte-RLE, which encodes them poorly:

1. **Strided float32 patterns with unit > 8** — e.g. a 3-float interleaved tensor
   (stride 12 bytes) where only one channel changes. Motifs cannot start (unit 12 > cap 8).
2. **Sparse-but-irregular changed float lanes** — lanes change at arbitrary positions, so
   there is no single repeating unit for a motif at all.

Empirical probe (256 lanes × 3 float32, channel-2 += 0.5, 3072 bytes, motif path):
- distinct 12-byte unit masks = 3 (essentially constant `{8,9,10,11}`), but the changed
  bytes' **values vary per lane** (different mantissas of `x` vs `x+0.5`).
- byte-RLE ≈ **2057 bytes** (each changed lane = ZeroRun(8)=2B + NonZeroRun(4)=6B ≈ 8B/unit).
- Motifs do **not** fire (unit > 8).

### 1.2 FloatRun 0x06 semantics

FloatRun treats the XOR stream as a run of **float32 lanes** (4-byte granularity, aligned
to the XOR-stream origin — i.e. `pos % 4 == 0`). It records, via an explicit **per-lane
presence bitmap**, exactly which lanes are non-zero, then packs the full 4 bytes of each
changed lane contiguously. Decode XORs each changed lane's 4 bytes back into the output.

This is *distinct* from motifs: motifs need a repeating unit; FloatRun handles **arbitrary
sparse lane selection** at float32 granularity via the bitmap, and is not bounded by the
2–8 unit cap.

### 1.3 Wire framing (mirrors the motif opcode idiom)

```
[0x06][flags:1][laneCount:7bit][laneBitmap: ceil(laneCount/8) bytes][packedXor: 4 * changedLanes]
```

- **opcode** (1B): `0x06` = `RLE_FloatRun`.
- **flags** (1B): reserved, always `0x00` (kept for idiom symmetry with 0x04/0x05;
  future bits e.g. half/lane-size). Decoder requires `flags == 0x00`.
- **laneCount** (7-bit varint): number of consecutive float32 lanes covered. Each lane = 4
  bytes; the FloatRun advances `pos` by `4 * laneCount`.
- **laneBitmap** (`ceil(laneCount/8)` bytes, LSB-first): bit `i` set ⇒ lane `i` is non-zero
  and its 4 XOR bytes are present in `packedXor`. Bit ordering: lane `i` → byte `i/8`,
  bit `i%8` (`1 << (i & 7)`).
- **packedXor** (`4 * popcount(bitmap)` bytes): the 4 XOR bytes of each set lane, in lane
  order. All 4 bytes are stored even if some are individually zero (lane is the unit).

FloatRun size = `1 + 1 + varint(laneCount) + ceil(laneCount/8) + 4*changedLanes`.
On the probe case: `1+1+2+96+ (256*4=1024)` = **1124 B** vs byte-RLE 2057 B → 45% smaller.

### 1.4 Trigger / selection (deterministic — must be identical C# ↔ Zig)

In `EncodeXorWithMotifs`, the FloatRun probe runs **only in the basic-RLE fallback branch**
(after motif `TryStart` has failed and the accumulator is inactive), so it never perturbs
the 45 existing motif/RLE vectors. Conditions, evaluated at the current `pos`:

1. Motif detection enabled (same gate as motifs) AND `pos % 4 == 0` AND
   `pos + 4*2 <= len` (need ≥ 2 lanes).
2. Take the **maximal** lane run: `laneCount = (len - pos) / 4` (all whole 4-aligned lanes
   from `pos` to the largest multiple of 4 ≤ `len`).
3. Compute `floatSize` (§1.3) and `rleSize = EstimateRLESizeForSpan(xor[pos .. pos+4*laneCount])`.
4. **Strict savings gate:** emit FloatRun **iff `floatSize < rleSize`** (strict improvement;
   no speculative emission, mirrors the motif `ShouldEmit` discipline but with a hard
   `<` instead of a tolerance threshold — FloatRun is opt-in only when it truly wins).
5. If emitted: advance `pos += 4*laneCount`, increment `FloatPatternCount`. Otherwise fall
   through to the existing basic ZeroRun/NonZeroRun emission for this position.

A dense region makes `floatSize ≈ everything` and the gate rejects → basic RLE. Trailing
zero lanes only cost bitmap bits; the gate absorbs that. Because the run is always maximal
from a 4-aligned `pos` and the gate is a pure size comparison over deterministic integer
arithmetic, C# and Zig select identically → byte-identical output.

### 1.5 Round-trip / decode

Decoder reads flags (must be 0), laneCount, bitmap, then for each set bit XORs the next 4
packed bytes into `output[pos + lane*4 .. +4]`; advances `pos += 4*laneCount`. Bounds
checks mirror the motif decoders. Exact inverse of encode ⇒ round-trips byte-exactly.

## 2. Implementation notes

- **C# (authoritative):** `Utils.cs` already had `RLE_FloatRun = 0x06`. Added encoder
  probe `TryEmitFloatRun` + emit in `Encoder.cs` fallback branch; decoder case in
  `Decoder.cs`; wired `FloatPatternCount`.
- **Zig (mirror):** `utils.zig` `RLE_FLOAT_RUN = 0x06` + `float_pattern_count`; encoder
  `tryEmitFloatRun` mirroring the C# probe byte-for-byte; decoder case in `decoder.zig`.
  Source-of-truth comment names the authoritative C# symbols (per TASK-0429).
- **Test vector:** `Test046_FloatRun_Stride12_f32` — 256×3 float32, channel-2 += 0.5,
  3072 bytes (< 4096 ⇒ motif/full-XOR path), exercises FloatRun.

### 2.1 Trigger refinement (no-papering fix)

First implementation gated FloatRun against byte-RLE over the *maximal* span. This
regressed an existing vector (test015: `16FF,16z,16FF,16z`): after the first motif,
FloatRun greedily swallowed the trailing `16z,16FF,16z` (FloatRun 21 B) where a
ZeroRun+motif+ZeroRun (10 B) was far cheaper → test015 grew 21→32 B. Root cause: the
maximal span can include a downstream motif-able block separated by a long zero gap.

Fix (deterministic, mirrored): FloatRun now (a) requires the **first lane at `pos` to be
changed** (leading zero lanes are left to the natural ZeroRun, which re-probes FloatRun at
the next changed lane) and (b) **trims trailing zero lanes** (`laneCount = lastChanged+1`).
A solid contiguous changed block then fails the `floatSize < rleSize` gate (byte-RLE/motif
wins) and is left alone. Verified: test015 back to 21 B; test046 starts with a 2-lane
ZeroRun then FloatRun.

## 3. Evidence

### 3.1 C# suite (`dotnet test DeltaZorTests`)
- **Before:** `Failed: 0, Passed: 99, Skipped: 10` (a0611ec baseline).
- **After:** `Failed: 0, Passed: 100, Skipped: 10` (+1 = Test046; updated 1 assertion in
  `MotifRepeatTests.MotifRepeat_PatternCounts_AreTracked` to accept FloatPatternCount as a
  legitimately-tracked RLE-stream opcode — not a parity vector, no weakening).

### 3.2 Zig suite (`zig build test`, zig 0.15.1, fresh `.zig-cache` + regenerated corpus)
- **EXIT=0, 0 Error(s).**
- create-delta byte-parity: **45 active vectors pass** (test008 isValid=false, excluded at
  baseline too) — was 45 before; now includes **test046 FloatRun** (byte-identical C#↔Zig,
  1131 B). round-trip: 45 pass. apply: 45 pass. allocation-free: pass.
- test013 (large 120000-B tensor, streaming path) unchanged at 80173 B — confirms FloatRun
  does not perturb the streaming path or existing motif vectors.

### 3.3 FloatRun firing (test046, 256×3 float32, ch2 += 0.5, 3072 B)
- delta = **1131 B**: header(5) + `[00 08]` ZeroRun(2 leading unchanged lanes) +
  `[06 00 FE 05 ...]` FloatRun (flags 0, laneCount 766) + bitmap + packed lane XORs.
- vs byte-RLE ≈ 2057 B for the same XOR → ~45% smaller; round-trips exactly.

### 3.4 Test additions
- `Test046_FloatRun_Stride12_f32` (TestGen, registered in Program.cs) — corpus vector.
- `MotifRepeatTests` assertion broadened to include FloatPatternCount.

## 4. Handoff packet
- **Branch:** `task-0361-floatrun-0x06` (local only, NOT pushed).
- **Files:** C# `Encoder.cs` (TryEmitFloatRun + fallback probe), `Decoder.cs` (0x06 case);
  Zig `encoder.zig` (tryEmitFloatRun + probe + SoT comment), `decoder.zig` (0x06 case),
  `utils.zig` (RLE_FLOAT_RUN, float_pattern_count). TestGen `Test046...` + `Program.cs`.
  `MotifRepeatTests.cs` assertion. This exec log.
- **Codex re-audit:** can rebuild + re-audit C# fully. Zig: codex sandbox cannot reliably
  spawn the zig subprocess (known limitation) — for the Zig lane, codex should do static
  faithfulness review of the encoder/decoder mirror against the C# source-of-truth plus
  rely on this lane's confirmed `zig build test` EXIT=0 / 45-vector byte-parity.
- **Confidence:** high — both toolchains green, byte-parity + round-trip verified on real
  runs, no existing vector regressed, FloatRun is a gated strict improvement.
</content>
</invoke>
