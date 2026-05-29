# TASK-0364 — Global + Planar arithmetic modes (0x09 / 0x0A), C# + Zig byte-parity

impl-kind: claude (opus dev lane). EPIC-0046 (arithmetic modes), opening after the EPIC-0045
opcode trio (FloatRun 0x06 / HalfRun 0x07 / ChannelRun 0x08) completed the 0x00–0x08 set.
Branch `task-0364-arithmetic-modes` (no push). Cross-kind: codex audit follows.

## 0. STOP-vs-GO analysis (done BEFORE implementing) — decision: **GO**

The mandatory STOP gate: arithmetic-delta encoding only pays off if DeltaZor's realistic target
data contains gradient/ramp/counter structure that the existing opcodes encode worse. I evaluated
this honestly against the actual pipeline, not from filenames.

### Why arithmetic CAN win here — the architectural reason

The entire DeltaZor pipeline is **XOR-delta based**: `CreateRLEDelta` computes `xor = old ^ new`
and every inner opcode (0x00–0x08) encodes that XOR stream and applies it via `output[...] ^=
xorByte`. Crucially, **XOR destroys arithmetic structure**: for a uniform additive shift
`new = old + k`, the XOR `old ^ (old + k)` is *carry-dependent noise* that varies per element.
Measured directly (1024 random int32, all +5):

```
base=143337951 next=143337956  xor= 59 0 0 0
base=150666398 next=150666403  xor= 61 0 0 0
base=1663795458 next=1663795463 xor= 5 0 0 0
base=1097663221 next=1097663226 xor=15 0 0 0  ...   (low byte = 59,61,5,15,31,5,11,125 — noise)
```

So the low byte of each int32 lane changes to an unpredictable value (motif/channel can't lock a
constant pattern; byte-RLE stores one byte per changed element). The **arithmetic** difference,
however, is a constant `+5` per lane — captured by a single opcode.

### Candidate-input evidence (measured against the LIVE encoder, not hand-waved)

| input (realistic) | new | existing best (XOR/RLE/motif) | arithmetic | result |
|---|---|---|---|---|
| 1 048 576 × int32, all +5 (Test010) | 4.19 MB | 5.26 MB (RLE) / 4.19 MB (FullReplace) | **14 B** | win ×300 000 |
| 1024 × int32, all +5 | 4096 | 1563 (motif path) | **13 B** | win ×120 |
| ramp int32 `i*10` → `+5` | 8192 | 10 277 | **13 B** | win |
| 2000 bytes, all +3 (byte wrap) | 2000 | 2008 | **13 B** | win |
| 4000-px RGBA, R+10 tint | 16 000 | 20 005 | **13 B** (planar) | win |
| 4000-px RGBA, R+10 G−6 B+1 | 16 000 | 20 005 | **13 B** (planar) | win |

And the YIELD direction (no arithmetic structure) correctly declines: random 2048 B → arith=0,
planar=0, falls through to RLE (2092 B); sparse → RLE; identical → ZeroRun.

**GO.** Arithmetic strictly wins by 2–6 orders of magnitude on every realistic gradient/counter/
tint input, for a first-principles reason (XOR linearises to noise under carry). This is not a
speculative mode — Test010 already shipped in the corpus *expecting* it (its `ExpectedDeltaSize`
metadata was 17, aspirational), Plan.md roadmaps it (items 11–12), and four skipped unit tests
documented the intent. The STOP condition does not trigger.

## 1. Design

### Placement — inner RLE opcodes, detected at the WHOLE-region level (not inside the XOR loop)

0x09/0x0A are inner RLE opcodes (as Utils.cs reserved them), but arithmetic structure lives in
old/new **directly** (additive), not in the XOR stream — so detection cannot live inside
`EncodeXorWithMotifs` (which only sees the XOR buffer, and only for ≤4096-byte buffers; the 1M
win bypasses motifs entirely via the streaming path). Detection therefore runs at the **top of
`CreateRLEDelta`** over the whole overlapping region `[0, minLength)`, where both old and new are
visible at any size. It emits a single opcode covering the region, then the shared length-op
handler (`AppendLengthOps`, refactored out — no behavior change) appends Extension/Truncation.

**Decode is additive, not XOR.** The RLE decoder pre-fills `output` with `oldData`; 0x09/0x0A read
the current output bytes (= old), add the step, write back. This is the key divergence from
0x00–0x08 (which XOR into output) and is what makes arithmetic representable as an inner opcode.

### Wire framing

```
0x09 Global :  [0x09][elemWidth:1][step: elemWidth bytes LE, two's-complement][laneCount:7bit]
0x0A Planar :  [0x0A][planeCount:1][steps: planeCount bytes][unitCount:7bit]
```

- **Global (0x09):** uniform additive step on fixed-width little-endian integer lanes. elemWidth
  probed in {4,2,1,8} (int32 first — the canonical counter/gradient case). Requires
  `minLength % w == 0`, ≥2 lanes, and EVERY lane sharing the identical wraparound difference
  `step = (newLane − oldLane) mod 2^(8w)`. Decode: `out_lane += step` (wraparound) per lane.
- **Planar (0x0A):** per-plane uniform byte step on interleaved byte planes. planeCount probed in
  {4,3,2} (RGBA/RGB/2). Requires `minLength % P == 0`, ≥2 units, and EVERY unit matching all P
  per-plane byte steps. Decode: `out[u*P+p] += steps[p]` (byte wraparound) — captures tints where
  each channel shifts by its own constant (including 0).

### Clamp-aware decision: **wraparound, NOT clamp**

The brief asked to be clamp-aware "as needed". Analysis: clamp (`255+10=255`) is **lossy** and
cannot round-trip — once two distinct old values clamp to the same new value, the additive step is
ambiguous on decode. Exact reconstruction requires two's-complement / byte **wraparound** (which
is what real counter/tensor/tint data does and what `(byte)(x + k)` produces in C#). So 0x09/0x0A
are wraparound-exact; clamp is deliberately NOT used (it would break the lossless contract). A
future clamp-aware *lossy* variant could use a reserved flag, but it is out of scope and not
needed for any winning case.

### Gate — strict improvement vs the XOR/RLE alternative

Each mode emits ONLY when its framed size is strictly smaller than the whole-region XOR byte-RLE
cost (`EstimateXorRleSizeWholeRegion` — a new allocation-free O(n) counter that reads old/new
directly, so it works for 1M+ buffers without materialising the XOR). For arithmetic data this
alternative is huge (one byte per changed lane + run overhead) while the opcode is ~10–13 B → the
gate always passes; for non-arithmetic data the detection fails outright (steps differ) before the
gate is even reached. A non-zero step is required (a zero step means new==old, which ZeroRun
encodes far cheaper). Strict-improvement-or-no-op — never a regression.

### Coexistence with the motif subsystem

Degenerate **all-zero-base uniform** patterns (e.g. `0x01` at every even byte ×51) are
simultaneously valid motifs AND valid arithmetic shifts, and arithmetic genuinely encodes them
smaller (5 B vs ~7 B). The motif-internals unit tests (`MotifTests`) craft exactly such synthetic
inputs to exercise the motif state machine. Following the existing test-scoping pattern in that
file (`EnableMotifDetection = false`, `CompressionThreshold = 2.0`), a new `EnableArithmeticDetection`
option (default **true**) lets those motif tests pin the path they test — analogous to how they
already force the RLE path. This is subsystem scoping, not papering: the real arithmetic behavior
is proven by the corpus vectors and the un-skipped arithmetic unit tests. Probe order is Global,
then Planar (Global subsumes the all-planes-equal-step case more compactly).

## 2. Implementation

**C# (authoritative)** — `src/csharp/DeltaZor/`:
- `Utils.cs`: `RLE_Arithmetic = 0x09`, `RLE_Planar = 0x0A` (docs rewritten from "Pending"),
  `ArithmeticElemWidths = {4,2,1,8}`, `PlanarPlaneCounts = {4,3,2}`,
  `EstimateXorRleSizeWholeRegion(old,new,len)` (NEW alloc-free whole-region gate cost).
- `DeltaZor.cs`: `OpCodeCounts.ArithmeticCount` / `.PlanarCount` (+ `TotalPatternCount`),
  `DeltaOptions.EnableArithmeticDetection` (default true).
- `Encoder.cs`: `TryEmitGlobalArithmetic` / `TryEmitPlanarArithmetic` (sources of truth) +
  `ReadLE` + `AppendLengthOps` (length-op refactor), probed at the top of `CreateRLEDelta`.
- `Decoder.cs`: `case RLE_Arithmetic` / `case RLE_Planar` — additive (`+=`) reconstruction,
  wraparound, full bounds + width/plane validation.

**Zig (byte-for-byte mirror)** — `src/zig/src/`:
- `utils.zig`: `RLE_ARITHMETIC` / `RLE_PLANAR`, `arithmetic_elem_widths`, `planar_plane_counts`,
  `OpCodeCounts.arithmetic_count`/`.planar_count`, `Options.enable_arithmetic_detection`.
- `encoder.zig`: `tryEmitGlobalArithmetic`, `tryEmitPlanarArithmetic`, `readLE`,
  `estimateXorRleSizeWholeRegion`, `appendLengthOps` (refactor), probed at top of
  `createRLEDeltaDirect`. Mirrors the C# source of truth.
- `decoder.zig`: `RLE_ARITHMETIC` / `RLE_PLANAR` decode cases (`+%` wraparound).

Bounded strictly to the 0x09/0x0A path + detection + the new gate estimator + vectors + exec log.
0x00–0x08 behavior untouched.

## 3. Corpus vectors (win + win + yield)

- **Test052 GlobalArithmetic_Int32Ramp** (1024 int32, +5): delta **13 B**, `ArithmeticCount==1`.
  Wire: `09 04 05 00 00 00 80 08` = 0x09, w=4, step +5 LE, laneCount 1024.
- **Test053 PlanarArithmetic_RgbaTint** (1000 px RGBA, R+10/G−6/B+1/A+0): delta **13 B**,
  `PlanarCount==1`. Wire: `0a 04 0a fa 01 00 e8 07` = 0x0A, P=4, steps, unitCount 1000.
- **Test054 Arithmetic_YieldsToXor** (2048 B fully-random change): delta **2092 B**, arith=0
  planar=0 — declines, falls to XOR/RLE (`01 2f ...` NonZeroRun). Gate guard.
- **Test010 Uniform_Int32_1M** (pre-existing, now realised): 4.19 MB → **14 B** via 0x09. Its
  stale `ExpectedDeltaSize` metadata corrected 17 → 14.

## 4. Verification (both toolchains, real)

- **C#:** `dotnet test ... --no-restore -m:1` → **111 passed, 0 failed, 8 skipped**. (Was
  105/0/10 baseline; +3 arithmetic unit tests now passing — Global, Planar, Yield un-skipped/added;
  motif-internals tests scoped with `EnableArithmeticDetection=false`, behavior preserved.)
- **Zig:** clean `.zig-cache` + `zig build test` → **EXIT=0**. build.zig regenerated the corpus
  from the current C# encoder; all vectors **create-delta byte-identical C#↔Zig**, apply, and
  round-trip pass (53/53/53 categories, including arithmetic 52/53/54 and Test010), zero leaks.

## 5. Deferred / not done

- **Clamp-aware (lossy) variant:** deliberately excluded — clamp can't round-trip (see §1).
  A reserved flag could carry it later if a lossy use case appears.
- **Per-run / local arithmetic (Plan items 13–14, 0x0B):** out of scope; 0x09/0x0A are
  whole-region. Mixed buffers (an arithmetic block + other changes) currently take the XOR path
  unless the WHOLE region is uniform — a per-run arithmetic inner opcode would capture partial
  blocks and is the natural EPIC-0046 follow-up.
- **elemWidth/planeCount probe sets** capped at the common shapes ({4,2,1,8} / {4,3,2}); the wire
  carries a full byte for each so wider shapes are representable, just not probed.
- STOP condition did NOT trigger: arithmetic captures a real, first-principles shape (uniform
  additive shift) that XOR-based opcodes encode catastrophically worse. 0x09/0x0A are worth keeping.

## Cross-kind audit (codex on claude impl)

### A. Additive decode correctness + round-trip

APPROVED. C# `ApplyRLEDelta` pre-fills `output` from `oldData` before the opcode loop, then the
0x09 and 0x0A cases diverge from the XOR opcodes by applying additive reconstruction. 0x09 reads
`elemWidth`, LE `step`, and `laneCount`, reconstructs each old lane from `output`, computes
`(cur + step) & widthMask`, and writes the result back LE. 0x0A reads `planeCount`, byte `steps`,
and `unitCount`, then writes `(byte)(output + step)` per plane. This is wraparound, not clamp.

The encoder derives the same modular step it later serializes: global uses `(newLane - oldLane)
mod 2^(8w)` for widths `{4,2,1,8}`, and planar uses byte subtraction for plane counts `{4,3,2}`.
Both probes verify every lane/unit in the whole overlapping region before emitting, so an emitted
arithmetic opcode satisfies `old + step == new` modulo the lane width for every covered element.

### B. Detection soundness - strict improvement + correct yield

APPROVED. Arithmetic detection is whole-region only and runs at the top of `CreateRLEDelta` when
`EnableArithmeticDetection` is true. It requires divisibility by the candidate width/plane count,
at least two lanes/units, at least one non-zero step, and a full-region uniformity check. There is
no sampled or approximate match path.

The emit gate is strict: `arithSize < EstimateXorRleSizeWholeRegion(...)` or
`planarSize < EstimateXorRleSizeWholeRegion(...)`; equality yields. Identical data yields through
the zero-step checks. Random/sparse/non-arithmetic data yields through the whole-region uniformity
checks and then falls back to the existing XOR/RLE/motif pipeline.

### C. C# <-> Zig faithfulness

APPROVED. Zig mirrors the C# probe order, width sets, plane sets, LE read/write, varint size
accounting, extension/truncation append, and additive decode. The wrapping semantics align:
Zig uses explicit `-%` and `+%`; the C# project does not enable checked overflow, so the current
unsigned arithmetic and byte casts use wraparound semantics. Both decoders accept the same 0x09
widths and the same 0x0A plane-count range; the emitters only probe `{4,3,2}` for planar.

### D. Coexistence + scope

APPROVED. `EnableArithmeticDetection` defaults true and only gates the new whole-region probes.
The motif tests that disable it are degenerate all-zero-base fixtures that are also valid arithmetic
shifts; disabling arithmetic there legitimately pins those tests to the motif state machine rather
than hiding a motif correctness issue. The pre-existing 0x00-0x08 decode cases are unchanged, and
the RLE length-change behavior was only factored into `AppendLengthOps` with equivalent counts.

### E. Independent re-run

NOT RERUN. Per the audit instructions, I did not run Zig and did not run any restoring dotnet
command. I relied on the orchestrator-confirmed C# `111 passed / 0 failed / 8 skipped` and clean
Zig `build test` byte-parity/round-trip result. I also did not run the optional C#
`--no-restore --no-build` test pass to preserve the requested read-only scope aside from this log
append.

### VERDICT

APPROVED. Arithmetic modes 0x09/0x0A are sound: additive wraparound decode is an exact inverse of
the verified whole-region step detection, emission is strictly smaller than the byte-RLE estimate,
and the C# and Zig implementations preserve EPIC-0044 byte-parity/round-trip. EPIC-0046 is opened;
orchestrator can merge `task-0364-arithmetic-modes` to `master` and close TASK-0364.
