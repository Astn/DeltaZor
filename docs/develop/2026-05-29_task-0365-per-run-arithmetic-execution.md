# TASK-0365 — Per-run arithmetic (RunArithmetic 0x0B) + clamp-aware — execution log

- Date: 2026-05-29
- Branch: `task-0365-per-run-arithmetic` (from `master` @ de76895)
- Epic: EPIC-0046
- impl-kind: claude (opus), DEV lane. Cross-kind codex audit follows.
- Toolchains run for real (no sandbox): `dotnet test --no-restore -m:1`, `zig build test`.

## Context (extends TASK-0364)

TASK-0364 added whole-region arithmetic: `0x09` GlobalArithmetic (uniform additive step on
fixed-width LE integer lanes) and `0x0A` PlanarArithmetic (per-plane byte step). Both require the
**entire** overlapping region to be one arithmetic progression and are emitted as the whole delta
(followed only by length ops). Detection reads old/new directly (XOR destroys arithmetic structure
via carries) and decode is additive on output pre-filled with old.

TASK-0365 adds **per-run / local** arithmetic: a buffer that is an additive progression for a
*segment* (e.g. bytes 100-199 all shifted by +10), interleaved with unchanged or unstructured
regions. Whole-region 0x09/0x0A cannot fire because uniformity breaks. We need a **segment-level**
additive opcode that coexists with ZeroRun/NonZeroRun in the streaming RLE loop.

## Design

### Opcode

`RunArithmetic = 0x0B`. The Plan.md §13-15 table lists nominal opcodes (`0x04`) that collide with
the live MOTIF opcode — those are illustrative, not authoritative. The authoritative reserved slot
per `Utils.cs` is "0x0B+ for future (Clamp-Aware, per-run arithmetic)". We take `0x0B`.

### Wire framing

```
[0x0B][flags:1][step:1][runLen:7bit]
```

- `flags` bit0: 0 = wraparound mode, 1 = clamp mode. Other bits reserved (must be 0; decoder
  rejects nonzero reserved bits).
- `step`: a single signed byte (two's-complement). For wraparound it is added mod 256; for clamp it
  is interpreted as i8 and added with saturation to [0,255].
- `runLen`: 7-bit varint, number of consecutive bytes the run covers. Must be >= MinRun (3) — below
  that the framing (4 bytes header) never beats a NonZeroRun.

This is a **byte-granular** per-run analogue of PlanarArithmetic with P=1, but segment-scoped and
emitted *inside* the RLE stream advancing `pos` by `runLen` (like ZeroRun/NonZeroRun).

### Decode (additive, NOT XOR — output pre-filled with old)

- Wraparound: `output[pos+i] = output[pos+i] +% step` (mod 256). Mirrors Planar P=1.
- Clamp: `v = (i32)output[pos+i] + (i32)(i8)step; output[pos+i] = clamp(v, 0, 255)`.

`output[pos+i]` is still the untouched `old` byte when this op runs (ops are sequential and
non-overlapping), so both modes are a **deterministic function of (old, step)** the decoder fully
possesses.

### Clamp-aware IS lossless (the crux)

TASK-0364 deferred clamp as "lossy / can't round-trip". That was correct for a clamp **applied as a
decode transform without verification**: many old values map to the same clamped new (250+10 and
255+10 both → 255), so clamp is not invertible *in the abstract*. But it does NOT need to be
invertible — the encoder only emits clamp mode when it has **verified** that for every byte in the
run `new[i] == clamp(old[i]+step, 0, 255)`. At decode the byte still holds `old[i]`, so recomputing
`clamp(old[i]+step)` reproduces `new[i]` **exactly**. Clamp is a deterministic forward function of
data the decoder owns; losslessness comes from encoder-side verification + exact replay, not from
invertibility. No exception list, no corruption. Implemented losslessly — Clamp test un-skipped.

### Detection / gate (strict-improvement-or-no-op, the standing discipline)

`TryEmitRunArithmetic` is probed in the whole-region direct path **after** 0x09/0x0A decline and
**before** the XOR/motif pipeline (so it never disturbs 0x00-0x0A emit/decode; the gate may consult
their cost). It greedily scans the region [0,minLength):

1. At each `pos`, find the longest run where a single step `s` satisfies, for all i,
   `new == old +% s` (wraparound) OR `new == clamp(old+s)` (clamp). The step is taken from the
   first differing byte; equal bytes (step 0) are not arithmetic runs (ZeroRun is cheaper) so a run
   starts only at a changed byte and `s != 0`.
2. Runs shorter than MinRun, and unchanged spans, are emitted with the **same ZeroRun/NonZeroRun
   XOR encoding** the baseline streaming path would produce.
3. The whole candidate stream (0x0B runs + ZeroRun/NonZeroRun fillers + length ops) is sized and
   compared against `EstimateXorRleSizeWholeRegion`. **Emit the whole plan only if it is strictly
   smaller**; otherwise decline entirely (no-op) and fall through to the unchanged XOR/motif path.
   At least one 0x0B run must be present, else there is nothing to win.

Wraparound is tried first per run (it is the cheaper/cleaner case and matches the +10 byte test);
clamp is tried when wraparound fails but every byte saturates consistently. Coexistence with
0x09/0x0A is deterministic: whole-region progressions are claimed by 0x09/0x0A first; only segmented
ones reach 0x0B.

## Implementation

- C# authoritative: `Utils.cs` (`RLE_RunArithmetic = 0x0B`, `RunArithmeticMinRun`,
  `EstimateRunArithmeticPlan`), `Encoder.cs` (`TryEmitRunArithmetic` + run-scan helpers),
  `Decoder.cs` (0x0B case), `DeltaZor.cs` (`RunArithmeticCount`, added to TotalPatternCount).
- Zig byte-for-byte mirror: `utils.zig` (`RLE_RUN_ARITHMETIC`, count), `encoder.zig`
  (`tryEmitRunArithmetic`), `decoder.zig` (0x0B case).
- Bounded: 0x00-0x0A emit/decode untouched.

## Tests un-skipped

- `PerRunArithmetic_DetectsAndAppliesLocalUniformChanges` — segmented +k byte run, asserts
  RunArithmeticCount==1, exact round-trip.
- `RunArithmeticOpcode_CorrectlyEncodesAndDecodes` — the +10 over [100,200) case, strengthened to
  assert the opcode fires + exact round-trip.
- `ClampAwareDetection_HandlesOverflowCorrectly` — a run that saturates at 255 (and at 0),
  asserts clamp-mode RunArithmetic fires + exact round-trip at both boundaries.

(`AutoModeSelection_ChoosesBestCompressionMode` stays as-is — out of scope for this task; it is a
generic selection placeholder, not part of the per-run/clamp contract.)

## Verification (both toolchains run for real)

### C# — `dotnet test --no-restore -m:1`

- `ArithmeticCompressionTests` filtered: 7 passed, 1 skipped (only `AutoModeSelection`, out of
  scope). `PerRunArithmetic`, `RunArithmeticOpcode`, `ClampAwareDetection` now PASS (un-skipped).
- Full suite: **114 passed, 0 failed, 5 skipped** (was 113 passed / 8 skipped pre-task; the 3
  per-run/run/clamp tests moved skipped→passing). No regression.
- One pre-existing test (`SevenBitEncoding_LargeNumbers_Works`) asserted a ZeroRun/NonZeroRun
  opcode breakdown over a 150-byte uniform +1 change that RunArithmetic now legitimately wins
  (strict size improvement, lossless). Its stated intent is "pure RLE behavior"; it already
  disabled motifs for that reason, so I added `EnableArithmeticDetection = false` to isolate the
  baseline — preserving exactly what it tests (not weakened; round-trip still asserted).

### Zig — `rm -rf testdata .zig-cache && zig build test` → **EXIT=0**

`zig build test` regenerates testdata from the current C# encoder (TestGen), so create-delta
asserts the C# delta is byte-identical to Zig's `createDelta`, plus apply + round-trip + leak-free.

- New vectors **Test055 RunArithmetic_LocalByteShift** (2048B, 800-byte +7 segment → **16-byte
  delta**; whole-region 0x09/0x0A correctly decline, 0x0B fires) and **Test056
  RunArithmetic_ClampBoundary** (1024B, ceiling clamp(+10) + floor clamp(-10) saturating runs →
  **21-byte delta**, clamp mode):
  - create-delta: C# ↔ Zig **byte-identical** (sizes 16 / 21 match exactly).
  - apply + round-trip: **exact** at both saturation boundaries (250→255, 255→255; 5→0, 0→0).
- All 56 vectors (existing 52 baseline + 4 arithmetic + the 2 new) pass create/apply/round-trip;
  zero leaks. EXIT=0.

### Per-run win + clamp lossless proof

- Per-run win: Test055's 800-byte arithmetic segment → 16 bytes total delta (the 0x0B run is 4
  bytes: `[0x0B][00][07][runLen]`), vs ~800+ bytes for the XOR/RLE alternative. The gate emits only
  because `planSize < EstimateXorRleSizeWholeRegion`.
- Clamp lossless: Test056 + `ClampAwareDetection_HandlesOverflowCorrectly` both round-trip
  byte-exact through saturated bytes — decode recomputes `clamp(old+step)` on the untouched old
  byte, reproducing the clamped new exactly. No exception list, no corruption.

## Result

RunArithmetic (0x0B) per-run + clamp-aware BOTH implemented losslessly; nothing descoped. Tests
PerRun/RunArithmetic/Clamp un-skipped and passing. C# authoritative, Zig byte-for-byte mirror.
Bounded to the per-run/clamp path; 0x00-0x0A emit/decode untouched.
