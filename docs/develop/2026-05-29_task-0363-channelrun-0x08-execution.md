# TASK-0363 — ChannelRun 0x08 (C# + Zig, byte-parity, all-opcode-aware gate)

impl-kind: claude (opus dev lane). EPIC-0045, ChannelRun 0x08, after FloatRun 0x06 (TASK-0361)
and HalfRun 0x07 (TASK-0362). Branch `task-0363-channelrun-0x08` (no push).

## 1. Design (written before coding)

### Problem ChannelRun targets — and why the existing opcodes can't reach it

ChannelRun targets **channel-interleaved byte data with a stride GREATER than the motif unit
cap (8)** where only a small, fixed set of byte channels changes per unit, each by a distinct
value. Canonical example: a 12-byte-per-element interleaved record (3× float32 planar-
interleaved, or a vertex struct) where only byte offset `c` of each 12-byte unit changes — XOR
stream `[c0,0,0,0,0,0,0,0,0,0,0,0, c1,0,...]`, stride 12, one changed byte per unit.

How the existing opcodes price this shape (verified numerically, N=32 units, span=384 B):

| opcode  | cost  | why it loses |
|---------|-------|--------------|
| byte-RLE | 160 | each changed byte is isolated → op+len+1 per change + ZeroRun between |
| motif   | 160 (= RLE) | **stride 12 > unit cap 8 → motif can never lock**, falls back to RLE |
| FloatRun | 143 | 4-byte lanes: packs the 3 zero bytes inside each changed lane (4 B/change) |
| HalfRun  | 92  | 2-byte lanes: packs 1 zero byte inside each changed half-lane + 1 bit/half-lane bitmap over the whole span |
| **ChannelRun** | **38** | knows the stride + changed-channel mask → packs exactly the 1 changed byte/unit, no per-lane bitmap |

ChannelRun is essentially **"varying-motif byte packing for strides > 8"** — the precise gap the
motif unit cap (8) leaves open. Motif already dominates strides ≤ 8 (it packs exactly the changed
bytes per unit with a per-unit mask); FloatRun/HalfRun lose on single-byte-per-unit shapes
because they pack whole 4-/2-byte lanes including the zero bytes inside the lane. ChannelRun only
fires where motif cannot (stride > 8) AND it beats the float-lane opcodes' wasted in-lane bytes.

This is a genuine shape the other four opcodes encode poorly → 0x08 is worth implementing (NOT a
no-op opcode; the STOP condition does not apply).

### Trigger / probe

In the basic-RLE fallback branch, probed AFTER HalfRun (0x07) and FloatRun (0x06) so those keep
priority. ChannelRun probes a fixed set of strides **strictly greater than the motif unit cap**:
`{12, 16, 9, 10, 11, 13, 14, 15}` (12 and 16 first — the common interleaved record widths). For
each candidate stride `S`:
  - derive the channel mask from the first unit `xor[pos .. pos+S]` (which byte offsets are
    nonzero); require ≥1 changed channel and require `xor[pos] != 0` is NOT required — instead
    the first UNIT must be non-empty (mask != 0), the natural anchor (mirrors FloatRun's
    "first lane changed" so leading all-zero regions are left to ZeroRun);
  - extend over consecutive units while the mask is **consistent** (every masked offset nonzero,
    every unmasked offset zero) — same discipline as varying motif, at stride > 8;
  - require ≥ 2 matched units; the run covers `unitCount * S` bytes.

Deterministic stride selection: the first stride in the probe list that produces a feasible run
AND passes the savings gate wins; ties are resolved by probe order (12 before 16, etc.). A single
`flags=0x00` byte is reserved (mirrors 0x06/0x07).

### Wire framing

```
[0x08][flags=0x00][stride:1 byte][channelMask: ceil(stride/8) bytes, LSB-first][unitCount:7bit][packed: popcount(mask) * unitCount bytes]
```

`stride` is a single byte (1..255; probe set keeps it 9..16). `channelMask` marks which byte
offsets within the unit changed (LSB-first across `ceil(stride/8)` bytes). `packed` holds the
changed bytes **unit-major, then channel-order** (same layout as varying motif): for each unit
`u` in `0..unitCount`, for each set channel offset in ascending order, one XOR byte. Decode XORs
each packed byte into `output[pos + u*stride + channelOffset]`.

### All-opcode-aware gate (the TASK-0361 lesson, applied from day one)

ChannelRun emits ONLY when its framed size is **strictly smaller than EVERY alternative** over the
exact same span `[pos, pos + unitCount*stride)`:

```
channelSize < min( rleSize, motifRleSize, floatSize, halfSize )
```

reusing the existing estimators:
  - `EstimateRLESizeForSpan` (byte-RLE)
  - `EstimateMotifRleSizeForSpan` (live motif/RLE encoder cost — already used by 0x06/0x07)
  - `EstimateFloatRunSizeForSpan` (FloatRun cost over the span — added for 0x07, reused)
  - `EstimateHalfRunSizeForSpan` (HalfRun cost — **added here**, mirroring EstimateFloatRunSizeForSpan)

Strict-improvement-or-no-op: ChannelRun never regresses size vs the existing pipeline, and yields
to motif on strides ≤ 8 shapes (those never enter ChannelRun's probe set) and to whichever of
RLE/motif/Float/Half is cheaper on any span. Coexistence with 0x06/0x07 is decided purely by
probe order (Half, then Float, then Channel in the fallback branch) + the `min(...)` gate.

## 2. Implementation

**C# (authoritative)** — `src/csharp/DeltaZor/`:
- `Encoder.cs`:
  - `EstimateHalfRunSizeForSpan(span, pos)` — NEW pure size counter, mirrors
    `EstimateFloatRunSizeForSpan` at 2-byte granularity (the HalfRun term of the ChannelRun gate;
    returns `int.MaxValue` when HalfRun is infeasible for the span).
  - `ChannelRunStrides = {12,16,9,10,11,13,14,15}` — probe set, strides > motif cap 8.
  - `TryEmitChannelRun(...)` — NEW probe + emit (source of truth). Derives the channel mask from
    the first unit, extends over consecutive units with the same mask, gates against
    `min(rle, motifRle, float, half)`, emits the framing, bumps `ChannelRunCount`.
  - `EncodeXorWithMotifs` — ChannelRun probed FIRST in the basic-RLE fallback branch (before
    HalfRun/FloatRun) because it carries the most complete gate. Probe order + the `min(...)` gate
    are the entire coexistence rule: ChannelRun pre-empts Half/Float only when strictly cheaper,
    else declines and they probe next.
- `Decoder.cs`: NEW `case RLE_ChannelRun` — reads `[flags][stride][channelMask][unitCount]`, XORs
  each packed byte into `output[pos + u*stride + channelOffset]`, validates `flags==0`,
  `unitCount>=2`, bounds.
- `DeltaZor.cs`: `ChannelRunCount` already existed; now incremented.

**Zig (byte-for-byte mirror)** — `src/zig/src/`:
- `utils.zig`: `RLE_CHANNEL_RUN = 0x08`, `OpCodeCounts.channel_run_count`.
- `encoder.zig`: `estimateHalfRunSizeForSpan`, `channel_run_strides`, `tryEmitChannelRun` (probed
  FIRST), all mirroring the C# source of truth.
- `decoder.zig`: `RLE_CHANNEL_RUN` decode case.
- `build.zig`: `generate-testdata` now builds with `--no-restore -m:1` (and builds DeltaZor.csproj
  first) — without it the step silently used a STALE TestGen assembly and regenerated an
  out-of-date corpus that would mask C#<->Zig divergence (caught here: stale corpus claimed test
  33 = 107, current encoder emits 75).

## 3. Parity + win/yield evidence

- **C# unit suite**: 105 passed, 0 failed, 10 skipped (`dotnet test ... --no-restore -m:1`). One
  incidental assertion broadened: `MotifRepeatTests.MotifRepeat_PatternCounts_AreTracked` (100-byte
  buffer, 1 byte changed every 10 = stride-10 channel shape now a strict-win ChannelRun) — accept
  ChannelRunCount in the opcode-mix OR; the `UsedRLE`/`CompressionType`/round-trip contracts are
  unchanged. (Mirrors the TASK-0361/0362 broadening precedent.)
- **Zig `zig build test`**: EXIT=0. 50 create-delta + 50 apply + 50 round-trip passes, 0 failures —
  ALL vectors (49 existing + new 50/51) byte-identical C#<->Zig and round-trip exact.
- **WIN — Test050 ChannelRun_Stride12_SingleChannel** (32×12 B, only byte 0 of each unit changes):
  delta = **43 B**, `ChannelRunCount == 1`. Alternatives over the span: byte-RLE 160, motif 160
  (stride 12 > cap 8, can't lock), FloatRun 143, HalfRun 92. ChannelRun (38 B over the span) wins.
- **YIELD — Test051 ChannelRun_YieldsToMotif** (32-pixel RGBA stride 4, only R changes): delta =
  **117 B**, `ChannelRunCount == 0`, `VaryingMotifCount == 16`. Stride 4 ≤ motif cap, never enters
  ChannelRun's probe set; motif owns it. Proves ChannelRun does not steal the motif path.
- **Bonus gate proofs (probed during dev, not vectored):** stride-12 ALL-channels-dense →
  ChannelRun declines (channelSize ≥ raw) → NonZeroRun; pre-existing Test033 (stride-16, 2 bytes/
  unit) now a strict-win ChannelRun (107 → 75 B) — a free improvement, gate-guaranteed non-regress.

## 4. Deferred / not done

- ChannelRun is **varying-only** (per-unit channel bytes). A uniform variant (single channel value
  repeated across all units) was not added — uniform stride-≤8 is already motif's domain, and a
  uniform stride-12 run would still be caught by the varying path (just stores N copies). A future
  uniform-ChannelRun flag could shave those; out of scope for 0x08's core gap-filling role.
- Probe set capped at strides 9..16 (the common interleaved record widths). Wider strides
  (>16) are representable on the wire (stride is a full byte) but not probed; extending the probe
  set is a cheap follow-up if a corpus need appears.
- STOP condition did NOT trigger: ChannelRun captures a real shape (stride > motif cap, single/few
  changed byte channels per unit) that RLE/motif/Float/Half all encode strictly worse. 0x08 is
  worth keeping.
