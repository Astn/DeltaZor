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

## Cross-kind audit (codex on claude impl)

### STOP / command evidence

Commands run from `C:\Users\austi\src\DeltaZor`:

```text
> git rev-parse HEAD
e5c886725fcb7911c38ad2b30a77c73dfd557f17

> git branch --show-current
task-0361-floatrun-0x06

> git status --short
<no output>
```

The branch and HEAD match the requested target (`task-0361-floatrun-0x06`, `e5c8867`), and
the worktree was clean before this audit append.

Touched-file evidence:

```text
> git diff --name-only HEAD^ HEAD
docs/develop/2026-05-28_task-0361-floatrun-0x06-execution.md
src/csharp/DeltaZor.TestGen/Program.cs
src/csharp/DeltaZor.TestGen/TestCases/Test046_FloatRun_Stride12_f32.cs
src/csharp/DeltaZor/Decoder.cs
src/csharp/DeltaZor/Encoder.cs
src/csharp/DeltaZorTests/UnitTests/MotifRepeatTests.cs
src/zig/src/decoder.zig
src/zig/src/encoder.zig
src/zig/src/utils.zig
```

### A. Zig FloatRun faithfulness to C# authoritative path

Static result: C# and Zig are byte-faithful for the implemented FloatRun algorithm.

Command evidence used:

```text
> rg -n "TryEmitFloatRun|FloatRun|FloatPatternCount|0x06" src -S
src/zig/src/decoder.zig:199:                    utils.RLE_FLOAT_RUN => { // float run 0x06
src/zig/src/encoder.zig:289:        if (tryEmitFloatRun(xor_data, pos, buffer, data_pos, counts, &float_covered)) {
src/zig/src/encoder.zig:333:fn tryEmitFloatRun(...)
src/csharp/DeltaZor/Encoder.cs:343:            if (TryEmitFloatRun(xorData, pos, writer, tempBuffer, ref counts, out int floatCovered))
src/csharp/DeltaZor/Encoder.cs:452:    private static bool TryEmitFloatRun(...)
src/csharp/DeltaZor/Decoder.cs:158:                case DeltaUtils.RLE_FloatRun:
```

Side-by-side read:

- Fallback order matches: both probe FloatRun only after motif extension/emission reset and
  after motif `TryStart` fails (`Encoder.cs:332-347`, `encoder.zig:279-292`).
- Alignment matches: both reject `(pos & 3) != 0`.
- Leading-lane requirement matches: both reject if lane 0 at `pos` has no changed byte.
- Trailing trim matches: both scan all remaining whole lanes, set `laneCount = lastChanged + 1`,
  and reject `laneCount < 2`.
- Size arithmetic matches: `1 + 1 + get7BitEncodedSize(laneCount) + ceil(laneCount/8) +
  4 * changedLanes`, strict reject on `floatSize >= rleSize`.
- Varint implementation matches existing 7-bit little-endian continuation encoding
  (`Utils.cs:250-261`, `utils.zig:58-67`).
- Bitmap layout matches: LSB-first per byte, `bitmap[l >> 3] |= 1 << (l & 7)` in both
  encoders and the same test in both decoders.
- Packed-lane order matches: changed lanes are copied in ascending lane order, 4 bytes per
  changed lane.

Test046 local artifact sanity:

```text
> Format-Hex -Path src/zig/testdata/test046.delta.bin -Count 96
0000000000000000 00 0C 00 00 00 00 08 06 00 FE 05 49 92 24 49 92
...
```

This decodes as header length 3072, RLE, `00 08` ZeroRun, then `06 00 FE 05` FloatRun
with flags 0 and laneCount 766. This matches the described Test046 framing. The local
`src/zig/testdata` directory is ignored (`git status --short --ignored ...` reported
`!! src/zig/testdata/`), but `src/zig/build.zig:46-68` regenerates it before Zig tests.

### B. Savings-gate soundness + regression fix

Static result: REJECTING issue found. The C# and Zig algorithms are faithful to each other,
but the trigger is not a sound strict-improvement gate against the full existing encoder.

The current fix prevents the exact test015 shape where a leading zero lane let FloatRun
start before a downstream motif. It does not prevent a changed first lane followed by an
internal zero gap and then a motif-able region. Because `TryEmitFloatRun` scans to the last
changed lane in the remaining XOR buffer, it can still swallow a later motif even though
motif `TryStart` failed only at the original `pos`.

Concrete adversarial XOR shape:

```text
bytes 0..3:     01 00 00 00        ; first lane changed, motif TryStart fails at pos 0
bytes 4..15:    00 ... 00          ; three zero lanes
bytes 16..415:  repeated 100 times: AA 00 00 00
```

Current FloatRun at `pos = 0`:

```text
laneCount = 104
changedLanes = 101
bitmapBytes = ceil(104 / 8) = 13
floatSize = 1 + 1 + 1 + 13 + (4 * 101) = 420
byte-RLE over same 416-byte span = 3 + 2 + 100 * (3 + 2) = 505
```

Since `420 < 505`, both C# and Zig emit FloatRun.

But the pre-FloatRun encoder path over the same data is much smaller:

```text
pos 0:  NonZeroRun len 1 = 3 bytes
pos 1:  ZeroRun len 15 = 2 bytes
pos 16: Uniform motif, unitSize 4, mask 1, repeatLength 100 = 6 bytes
total = 11 bytes
```

The motif starts at `pos = 16`, not `pos = 0`, so the current "after TryStart fails"
ordering does not protect it. This is the same class of regression as the no-papering note
(maximal FloatRun swallowing a downstream motif), only with a changed first lane instead of
a leading zero lane. It is not C#<->Zig parity divergence; it is a shared greedy-selection
bug that the current corpus does not exercise.

The gate is therefore strict only versus byte-RLE, not versus the existing motif/RLE
encoder alternative. FloatRun is not yet a guaranteed strict-improvement opcode.

Recommended fix direction: before emitting a maximal FloatRun, either cap the candidate
before the first downstream motif-start position, or compare against the actual motif/RLE
cost over the candidate span rather than byte-RLE alone.

### C. Decode correctness + round-trip

Static result: decode is correct for encoder-produced FloatRun streams.

Both decoders:

- require flags `0x00`;
- require `laneCount >= 2`;
- compute span as `laneCount * 4` and bounds-check against output length;
- read `ceil(laneCount/8)` bitmap bytes;
- walk lanes in ascending order;
- leave clear bitmap lanes unchanged, which is correct because the output is preloaded with
  old data and clear lanes mean zero XOR;
- XOR exactly four packed bytes for each set lane;
- advance `pos += laneCount * 4`.

The orchestrator-confirmed round-trip coverage is accepted here: C# `100 passed / 0 failed /
10 skipped`; Zig `EXIT=0`, all 45 active create-delta vectors byte-identical plus
round-trip exact, including Test046. I did not rerun tests.

### D. Scope + anti-papering

Static result: scope is bounded as requested, but the strict-improvement claim is not.

Command evidence:

```text
> git diff --stat HEAD^ HEAD
9 files changed, 454 insertions(+), 4 deletions(-)
```

The source changes are limited to the expected C# encoder/decoder, Zig encoder/decoder/utils,
TestGen registration and Test046, one MotifRepeatTests assertion, and the execution log. I
found no implementation of 0x07/0x08/0x09/0x0A and no decode-path rewrite for existing
opcodes:

```text
> rg -n "RLE_HalfRun|RLE_ChannelRun|RLE_Arithmetic|RLE_Planar|0x07|0x08|0x09|0x0A|RLE_HALF|RLE_CHANNEL|RLE_ARITH|RLE_PLANAR" src/csharp/DeltaZor src/zig/src -S
src/csharp/DeltaZor/DeltaZor.cs:122:        public int HalfPatternCount { get; set; } // 0x07 (Planned)
src/csharp/DeltaZor/DeltaZor.cs:123:        public int ChannelRunCount { get; set; } // 0x08 (Planned)
src/csharp/DeltaZor/Utils.cs:53:        RLE_HalfRun =
src/csharp/DeltaZor/Utils.cs:57:        RLE_ChannelRun =
src/csharp/DeltaZor/Utils.cs:61:        RLE_Arithmetic =
src/csharp/DeltaZor/Utils.cs:65:        RLE_Planar =
```

`MotifRepeatTests` changed a broad "some RLE-stream pattern count exists" assertion from
Zero/NonZero-only to Zero/NonZero/Float. That is a legitimate broadening for that test; it
does not assert motif behavior specifically, and it still requires RLE and compression type
`"RLE"`.

Test046 is a real corpus case in TestGen (`Program.cs:217-218`) with `ExpectedDeltaSize =>
1131`, and the ignored regenerated corpus present locally has manifest `testId: 46`,
`deltaSize: 1131`, and real base/next/delta files. `build.zig` still always regenerates the
corpus from the current C# encoder before Zig tests (`src/zig/build.zig:46-68`).

### E. Independent re-run

I did not run `dotnet test`, `dotnet build`, or Zig. Per the audit instructions, I relied on
the orchestrator-confirmed dynamic results:

```text
C#: dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj --no-restore -m:1
    100 passed / 0 failed / 10 skipped, EXIT 0

Zig 0.15.1 build test:
    EXIT=0, 45 active create-delta vectors byte-identical C#<->Zig plus exact round-trip,
    Test046 included, test008 excluded as pre-existing
```

### VERDICT

REJECTED.

FloatRun 0x06 appears byte-faithful between C# and Zig for the implemented algorithm, and
the 0x06 decoder reconstructs encoder-produced streams correctly. However, the savings gate
is still not sound as a strict-improvement opcode: a changed first lane can cause maximal
FloatRun to swallow a later motif-able region and regress size badly. This violates the
TASK-0361/EPIC-0045 requirement that FloatRun be a strict improvement or a no-op.

## Remediation (codex REJECT B) — make the FloatRun gate motif-aware

- **Date:** 2026-05-29 · **Impl-kind:** claude (opus, no-sandbox dev remediation lane)
- **Base:** `be41783` (impl `e5c8867` + codex audit appendix). Branch `task-0361-floatrun-0x06`.

### R.1 Reproduction (no-papering — proved real BEFORE fixing)

Built codex's adversarial XOR shape as a 416-byte vector (all-zero base ⇒ XOR == next):
byte 0 = `0x01` (first lane changed; motif `TryStart` fails at byte 0), bytes 4–15 = three
zero lanes, bytes 16–415 = 100× `AA 00 00 00` (a Uniform motif, unit 4). Ran it through the
**current** encoder (`be41783`):

```
deltaSize = 425   FloatPatternCount = 1
payload:  06 00 68 ...   (FloatRun, laneCount 0x68 = 104 — swallowed the whole region)
```

The optimal encode is `NonZeroRun(1) + ZeroRun(15) + UniformMotif(100)` = **16 bytes**.
Current = **425 bytes** → a **409-byte regression**, exactly codex's mechanism: the maximal
lane-run scans to the last changed lane and swallows the mid-span Uniform motif because motif
`TryStart` failed only at the *original* `pos`. Finding CONFIRMED reproducible.

### R.2 Fix — gate vs the motif/RLE alternative (approach b), not approach a

Approach (a) (cap the run before the first byte where `TryStart` succeeds) was implemented
first and **REJECTED in self-test**: it regressed the legitimate Test046 win 1131 → 1138 B.
Root cause — `TryStart` succeeding ≠ a motif is a net win. In Test046 (stride-12 float,
per-lane-varying mantissas) a *varying* masked motif `TryStart`s mid-span but does not extend
into a win; yielding to it fragmented the FloatRun and cost 7 bytes more. Capping on "motif
*could* start" is unsound.

Adopted approach **(b)**: FloatRun emits iff `floatSize < min(rleSize, motifRleSize)` where
`motifRleSize = EstimateMotifRleSizeForSpan(span)` — a new pure size counter that runs the
**actual live motif + byte-RLE state machine** (TryExtend / ShouldEmit / EmitMotif sizing +
ZeroRun/NonZeroRun fallback) over the candidate span *without* the FloatRun probe (no
recursion, never writes bytes). This prices the genuine alternative: the codex Uniform block
costs ~6 B (gate rejects FloatRun → motif path wins, 16 B total); Test046's short varying
motifs cost MORE than the unified FloatRun (gate accepts → FloatRun still wins, 1131 B). So
FloatRun is now a strict improvement over **every** alternative the pipeline would produce
for that span, or a no-op. C# `EstimateMotifRleSizeForSpan` + `MotifEmitSize` are the source
of truth; Zig `estimateMotifRleSizeForSpan` + `motifEmitSize` mirror them byte-for-byte.

### R.3 New permanent corpus vector

`Test047_FloatRun_YieldsToMidSpanMotif` (TestGen, id 47, registered in Program.cs) encodes
the R.1 reproduction. `ExpectedDeltaSize = 16`; it must emit NonZeroRun+ZeroRun+UniformMotif
(FloatPatternCount 0) and be byte-identical C#↔Zig + round-trip exact — locking the
regression out permanently.

### R.4 Evidence (real runs, both toolchains, --no-restore)

- **Repro before/after:** 425 B (FloatRun swallow) → **16 B** (FloatRun yields, Uniform motif).
- **C#** `dotnet test … --no-restore -m:1`: **Failed 0, Passed 101, Skipped 10** (was 100;
  +1 = Test047).
- **Zig** `zig build test` (fresh `.zig-cache`/testdata), zig 0.15.1: **EXIT=0, 0 Error(s)**;
  46 create-delta vectors byte-identical C#↔Zig (45 prior + Test047; test008 excluded as at
  baseline), round-trip + apply + allocation-free all pass.
- **Test046 UNCHANGED:** still FloatRun, `06 00 FE 05` (laneCount 766), **1131 B**,
  byte-identical C#↔Zig (the laneCount/size match the codex-audited baseline exactly).
- **Test047:** `01 01 01 | 00 0F | 04 80 64 04 01 AA` = 16 B, FloatPatternCount 0, both langs.
- test013 (streaming 120000 B) unchanged at 80173 B; test015 unchanged at 21 B.

### R.5 Scope

`git diff --stat HEAD`: `Encoder.cs` (gate + 2 helpers), `encoder.zig` (mirror), `Program.cs`
(+Test047 registration), new `Test047...cs`. No decoder/utils/0x07/0x08 changes. Decode path
untouched (the fix is encoder-side selection only; the wire format is identical).

**Confidence:** high — regression proved with a concrete vector, root-caused (TryStart ≠
net-win), fixed at the gate (strict-improvement vs the real motif/RLE alternative), both
toolchains green on real runs, byte-parity + round-trip verified, Test046 byte-identical.

## Cross-kind RE-AUDIT (codex on claude remediation)

- **Date:** 2026-05-29
- **Branch/HEAD checked:** `task-0361-floatrun-0x06` / `6df1ac4bfcf3ac17216e9b121bd44c85cb2b396b`
- **Scope:** static re-audit of the claude remediation to codex's prior REJECT. No graph update. I did not spawn another codex.

### A. Gate soundness

APPROVED. The original gap is closed.

The live FloatRun gate in `Encoder.cs` now computes `floatSize`, `rleSize`, and
`motifRleSize`, and emits only if `floatSize < rleSize` and `floatSize < motifRleSize`
(`TryEmitFloatRun`, lines 548-555). This directly blocks the prior MID-SPAN failure mode:
a changed first lane can no longer let the maximal FloatRun swallow a later motif-able block
unless the FloatRun is still strictly smaller than the motif/RLE encoding of that same span.

`EstimateMotifRleSizeForSpan` is a faithful pure-size version of the live
`EncodeXorWithMotifs` accumulator loop with the FloatRun probe removed:

- same inactive starting state;
- same `TryExtend` before `ShouldEmit`;
- same `ShouldEmit` threshold and `MotifEmitSize` framing;
- same reset behavior when a motif cannot be extended and is not worth emitting;
- same `TryStart` probe order through the live `MotifAccumulator`;
- same ZeroRun/NonZeroRun fallback sizing; and
- same trailing active-motif flush.

For this gate, the unsafe estimator error would be over-counting the motif/RLE alternative,
because that could make `floatSize < motifRleSize` true when the real motif/RLE path was
actually cheaper. I found no over-count: `MotifEmitSize` matches `EmitMotif`'s opcode, flags,
repeat varint, unit varint, optional mask varint, and packed data length; fallback RLE sizing
matches `DeltaUtils.EstimateRLESizeForSpan` and the live fallback writer.

### B. Test047 and Test046

APPROVED. `Test047_FloatRun_YieldsToMidSpanMotif` is a real TestGen vector, registered in
`Program.cs`, with the adversarial 416-byte XOR shape: byte 0 changed, bytes 4-15 zero, and
100 repeats of `AA 00 00 00` beginning at byte 16. Static sizing confirms the repaired gate:
the old FloatRun candidate is 420 payload bytes, while the motif/RLE alternative is
`NonZeroRun(1) + ZeroRun(15) + UniformMotif(100)` = 11 payload bytes, so the 5-byte delta
header makes the expected total 16 bytes and `FloatPatternCount` remains 0.

Test046 remains the legitimate FloatRun win. The new estimator does not cap on a mere
`TryStart`; it prices the whole motif/RLE alternative. That preserves the stride-12 float
case where short mid-span motif starts are not a net win, leaving the `06 00 FE 05`
FloatRun and 1131-byte result unchanged per the orchestrator run.

Non-blocking note: the Test047 source comment still says "caps before the first motif-able
byte position", but the actual implementation and remediation appendix use approach (b):
yield only when the priced motif/RLE alternative beats FloatRun.

### C. C#<->Zig estimator faithfulness

APPROVED. The new Zig `motifEmitSize`, `estimateMotifRleSizeForSpan`, and FloatRun gate
match the C# source of truth:

- same `MotifAccumulator` fields and lifecycle;
- same unit probe order 2..8 and density pruning;
- same full/uniform and masked/varying checks;
- same `shouldEmit` size arithmetic and `-0.1` threshold;
- same motif emit-size formula;
- same fallback RLE run accounting; and
- same strict `floatSize >= rleSize` / `floatSize >= motif_rle_size` rejection tests.

I did not find a C#<->Zig divergence risk in the new estimator. One stale Zig header comment
still summarizes FloatRun as byte-RLE gated only, but the executable gate and local comments
below it are motif-aware.

### D. Decode, scope, no-papering

APPROVED. `git diff be41783..6df1ac4` is bounded to the expected files:
`Encoder.cs`, `encoder.zig`, `Program.cs`, new `Test047...cs`, and this execution log.
There are no decoder, utils, 0x07, or 0x08 changes in the remediation diff. The 0x06 wire
format is unchanged; this is encode-side selection only. `git diff --check` reported no
whitespace errors. I found no weakened or skipped vector in the remediation diff.

Approach (a) was explicitly rejected in the remediation notes for the right reason:
`TryStart` success is not equivalent to a profitable motif. Approach (b), pricing the live
motif/RLE alternative, is the sound fix for the prior REJECT.

### E. Independent rerun

APPROVED. I relied on the orchestrator-confirmed clean runs as requested: C#
`dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj --no-restore -m:1` with
101 passed / 0 failed / 10 skipped, and Zig 0.15.1 `zig build test` EXIT=0 with 46
create-delta vectors byte-identical C#<->Zig plus exact round-trip, including Test046 and
Test047. I did not run any restoring `dotnet` command.

### VERDICT

**APPROVED.** Codex's prior REJECT is resolved. FloatRun 0x06 is now a motif-aware strict
improvement gate over the live byte-RLE/motif alternative for the candidate span, and the
new C# and Zig implementations preserve EPIC-0044 byte parity on the orchestrator-verified
corpus. Orchestrator may merge `task-0361-floatrun-0x06` to `main` and close TASK-0361.
