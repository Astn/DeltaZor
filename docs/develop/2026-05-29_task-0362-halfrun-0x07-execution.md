# TASK-0362 — HalfRun 0x07 (C# + Zig, byte-parity, motif-aware gate)

- **Epic:** EPIC-0045 (Float/Half/Channel pattern detection 0x06–0x08)
- **Branch:** `task-0362-halfrun-0x07` (off `master` @ `51a9d41`)
- **impl-kind:** claude (opus). Cross-kind codex audit follows.
- **Predecessor:** TASK-0361 FloatRun 0x06 (`51a9d41`). HalfRun is the float16 (2-byte lane)
  analogue, built on the same motif-aware-gate discipline that was the hard-won codex
  REJECT-B fix on 0361.

## 1. Design (written before coding — design-first discipline)

### 1.1 What HalfRun captures that FloatRun / motif / byte-RLE cannot

HalfRun treats the XOR stream as **float16 (2-byte) lanes** and emits a per-lane bitmap +
the 2 XOR bytes of each changed lane. It targets XOR shapes with **2-byte-aligned sparse /
irregular lane changes** that:

- **motif (unit cap 8)** cannot lock onto — the changed-lane period exceeds 8 bytes, or the
  changed half alternates position (low/high) so no fixed mask repeats;
- **byte-RLE** encodes expensively — isolated 2-byte nonzero runs separated by zero gaps pay
  `op+len+2` per change plus a zero-run between each;
- **FloatRun (4-byte lanes)** encodes wastefully — a changed half forces FloatRun to spend
  the **whole 4-byte lane** (2 wasted bytes when the sibling half is zero).

For **fully-changed 4-byte floats** (e.g. Test046's +0.5f delta, where both halves of each
changed float are nonzero) HalfRun is **strictly worse** than FloatRun: same packed-data
bytes (2 halves × 2B = 4B = the float lane) but ~2× the bitmap bits. The gate (§1.3) makes
HalfRun **yield** to FloatRun in exactly that case. So HalfRun is not redundant: it wins on
genuinely-2-byte-granular sparse shapes and yields everywhere FloatRun/motif/RLE already win.

### 1.2 Wire framing (mirrors the FloatRun 0x06 idiom at 2-byte granularity)

```
[0x07][flags:1=0x00][laneCount:7bit][laneBitmap: ceil(laneCount/8) bytes][packedXor: 2*changedLanes]
```

- **opcode** `0x07` = `RLE_HalfRun`.
- **flags** reserved, always `0x00`; decoder requires `== 0x00`.
- **laneCount** 7-bit varint: number of consecutive float16 lanes covered (lane = 2 bytes);
  advances `pos` by `2 * laneCount`.
- **laneBitmap** `ceil(laneCount/8)` bytes, LSB-first (lane `i` → byte `i/8`, bit `i%8`):
  bit set ⇒ lane `i` is non-zero and its 2 XOR bytes are present in `packedXor`.
- **packedXor** `2 * popcount(bitmap)` bytes: the 2 XOR bytes of each set lane, in lane order.

HalfRun size = `1 + 1 + varint(laneCount) + ceil(laneCount/8) + 2*changedLanes`.

### 1.3 Selection relationship vs FloatRun — the deterministic, no-double-fire rule

HalfRun is probed **FIRST** in the basic-RLE fallback branch (before FloatRun), at
**2-aligned** positions, with the first half-lane changed. It emits ONLY when

```
halfSize < min(rleSize, motifRleSize, floatSize)
```

over the candidate span, where:
- `rleSize` = `EstimateRLESizeForSpan` (byte-RLE),
- `motifRleSize` = `EstimateMotifRleSizeForSpan` (the live motif+RLE encoder cost — reused
  verbatim from TASK-0361; this is the codex-REJECT-B motif-aware gate),
- `floatSize` = `EstimateFloatRunSizeForSpan` (the FloatRun framing cost over the SAME span,
  a pure size counter, no emission).

The `floatSize` term is the new piece that resolves the HalfRun-vs-FloatRun relationship:
- On **fully-4-byte-dense** shapes (Test046), `halfSize > floatSize` ⇒ HalfRun **declines**,
  the second probe (FloatRun, gate unchanged) fires. FloatRun's vector is preserved.
- On **2-byte-sparse** shapes (Test048), `halfSize < floatSize` and `< motif/RLE` ⇒ HalfRun
  fires; the position is consumed so FloatRun never sees it. No double-fire.

FloatRun's own gate is left unchanged (`floatSize < min(rleSize, motifRleSize)`); it is
correct because HalfRun has already declined whenever it would have been the better choice.
Both gates are pure deterministic integer arithmetic ⇒ C# and Zig select identically ⇒
byte-identical output.

Leading-zero-lane and trailing-zero-lane discipline mirrors FloatRun: require the first
half-lane at `pos` changed (leading zero lanes left to the natural ZeroRun, which re-probes
at the next changed lane), and trim trailing zero lanes (`laneCount = lastChangedLane + 1`).

`EstimateFloatRunSizeForSpan(span @ pos)`: if `pos` is 4-aligned and the span length is a
multiple of 4, compute FloatRun's framing (count changed 4-byte lanes, trim trailing) exactly
as FloatRun would; else FloatRun cannot represent the span identically ⇒ return a sentinel
"infeasible" (treated as +∞, so HalfRun is not blocked by an alternative FloatRun cannot
actually emit). Deterministic and mirrored byte-for-byte in Zig.

### 1.4 Round-trip / decode

Decoder reads flags (must be 0), laneCount, bitmap, then for each set bit XORs the next 2
packed bytes into `output[pos + lane*2 .. +2]`; advances `pos += 2*laneCount`. Bounds checks
mirror the FloatRun decoder. Exact inverse of encode ⇒ round-trips byte-exactly.

## 2. Corpus vectors

- **Test048_HalfRun_SparseStride11_f16 (WIN):** 256 float16 lanes (512 B), changed lanes at
  the irregular set `{ i : i ≡ 9 (mod 11) }` (period 22 bytes > motif unit cap 8), each lane
  XORed with a per-lane-varying nonzero half value, isolated by zero lanes. HalfRun wins over
  FloatRun (4-byte lanes waste the sibling half), motif (no lock at period 11), and byte-RLE
  (many isolated 2-byte runs). Proves the win; `HalfPatternCount == 1`.
- **Test049_HalfRun_YieldsToFloatRun (YIELD):** a fully-4-byte-dense strided shape (the
  Test046 idiom) where each changed float's BOTH halves are nonzero. `halfSize > floatSize`
  ⇒ HalfRun yields; FloatRun fires. Proves the gate's FloatRun term. `HalfPatternCount == 0`,
  `FloatPatternCount == 1`.

## 3. Evidence

### 3.1 C# suite (`dotnet test … --no-restore -m:1`)
- **Before (master @ 51a9d41):** `Failed: 0, Passed: 101, Skipped: 10`.
- **After:** `Failed: 0, Passed: 103, Skipped: 10` (+2 = Test048, Test049).
- One pre-existing unit test, `MotifTests.MotifDetection_UnitSizeOver32_FallsBackToRLE`,
  asserted `NonZeroRunCount > 0` for an `unitSize=33 > cap` change at an even (2-aligned)
  offset; that change is now legitimately encoded as a HalfRun (strict size win), so the
  over-specific assertion was broadened to accept any tracked non-motif fallback opcode
  (NonZeroRun OR HalfRun OR FloatRun) — exactly the TASK-0361 precedent (MotifRepeatTests
  L159). The test's real contract (no motif emitted + exact round-trip) is unchanged and
  still asserted. No parity vector weakened.

### 3.2 Zig suite (`zig build test`, zig 0.15.1, fresh `.zig-cache` + regenerated corpus)
- **EXIT=0, 0 Error(s).** create-delta byte-parity: **48 active vectors** (test008
  isValid=false, excluded) — all byte-identical C#↔Zig, incl. the new **test048/test049**.
  round-trip: 48 pass. apply: 48 pass. allocation-free: pass.

### 3.3 The win, the yield, and a free improvement (opcode walk of the emitted deltas)
- **Test048 (WIN):** `ZeroRun(18B=9 lanes) + HalfRun(laneCount=243, changedLanes=23) +
  ZeroRun(8B)` = **90 B**. FloatRun over the same span would cost ~112 B (4-byte lanes waste
  the zero sibling half), motif cannot lock at period 22, byte-RLE pays per isolated 2-byte
  run. `HalfPatternCount == 1`, byte-identical C#↔Zig.
- **Test049 (YIELD):** dominant op is `FloatRun(laneCount=253, changedLanes=85)`, **no 0x07
  in the stream**; total **389 B**. Both float16 halves of each changed word are nonzero, so
  `halfSize > floatSize` ⇒ the HalfRun gate's FloatRun term rejects, HalfRun declines, the
  FloatRun probe (second) fires. `HalfPatternCount == 0`, `FloatPatternCount == 1`. Proves
  the deterministic no-double-fire selection.
- **Free win on a pre-existing vector:** **test33** (D512 Vertical 2 by 14: 2-byte XOR flips
  on a 16-byte stride) now encodes as `HalfRun(laneCount=249, 32 changed) + ZeroRun` = **107
  B**, down from **154 B** (FloatRun) — a strict improvement, byte-identical C#↔Zig,
  round-trips exact. No vector regressed.

### 3.4 What's deferred
- ChannelRun 0x08 (next opcode) — untouched. No changes to 0x06/0x08 framing or gates.
- HalfRun flags byte is reserved `0x00` (decoder enforces); no half-specific flag bits used.
