# TASK-0429 — Reconcile C#↔Zig encoder for byte-identical create-delta (EPIC-0044)

- **Date:** 2026-05-28
- **Impl-kind:** claude (Opus 4.8, no sandbox — real zig 0.15.1 + dotnet build/test)
- **Branch:** master (committed, not pushed)
- **Status:** review

## The defect (from TASK-0405 / TASK-0430)

`zig build test` (0.15.1) FAILED the create-delta byte-compare: C# and Zig produced
different delta BYTES for the same input. Round-trip/decode parity held (both produce
valid deltas) — they made different ENCODING choices. Known divergent: test004
(Mixed 1KB, C#=15B vs Zig=29B), Test044, Test045.

## Diagnosis — NOT threshold drift; a structural algorithm divergence

The brief's "strong lead" (Zig `motif_savings_threshold=-0.5` vs C# `-0.1`) was a
**false lead**. The `-0.1f` in the brief is the default parameter of C#'s
**DEAD** `ShouldEmit` helper. Systematic constant diff:

| Constant | C# (Utils.cs) | Zig (old encoder.zig) | Match? |
|---|---|---|---|
| MotifProbeCount | 7 | 7 | ✅ |
| MotifUnitSizes | {4,8,2,3,5,6,7} | {4,8,2,3,5,6,7} | ✅ |
| MotifDensityThreshold | 0.7f | 0.7 | ✅ |
| MotifSavingsThreshold (const) | -0.5f | -0.5 | ✅ |
| MotifMinStreak | 2 | 2 | ✅ |
| MaxMotifStreak | 50 | 50 | ✅ |

All declared constants matched. The root cause was **two different algorithms**:

- **C# live path** = `MotifAccumulator` (`Encoder.cs` `TryStart`/`TryExtend`/`ShouldEmit`,
  called from `EncodeXorWithMotifs`). Probes unit sizes **2..8 ASCENDING** (picks the
  smallest viable unit), grows the streak **one unit at a time with NO MaxMotifStreak
  cap**, and emits with savings threshold **-0.1** (the `ShouldEmit` default).
- **Zig** mirrored C#'s **DEAD** `FindMotifCandidate` method (greedy, probe order
  `{4,8,2,3,5,6,7}`, savings threshold the `-0.5` const, **capped at MaxMotifStreak=50**).
  C#'s own `FindMotifCandidate` is never called by the live encoder.

### test004 worked example (XOR = `ff00` × 256, then 512 zero bytes)

- **C# (accumulator):** probes u=2 first → masked uniform motif unit=2, repeat=256,
  mask=1 → 9 bytes + zero-run → **15 B total**.
- **Zig (FindMotifCandidate):** probes u=4 first → unit=4 mask=5, capped at 50 reps →
  region split into 50+50+28 = **three** motifs → ~21 B + zero-run → **29 B total**.

Two divergences combine: (1) probe order picks unit=4 vs unit=2; (2) the 50-cap forces
splitting. Both stem from Zig mirroring the wrong (dead) C# function.

## Fix — single source of truth = the LIVE C# accumulator

Rewrote Zig `encodeXorWithMotifsDirect` + added a `MotifAccumulator` struct
(`tryStart`/`tryExtend`/`shouldEmit`/`emitMotif`) that mirrors `Encoder.cs`
`MotifAccumulator` exactly:

- `tryStart` probes u=2..8 ascending; requires `pos+8 <= len`; per-unit requires
  `pos+2u <= len`, density prune `>= 0.7` for masked, first-repeat consistency check.
- `tryExtend` grows the streak unbounded (no cap), re-running `checkUniform` for
  masked-non-uniform motifs.
- `shouldEmit` uses savings threshold **-0.1** (matching the C# default).
- Control flow: extend → else emit+reset → else reset → else start → else RLE run;
  plus a trailing `shouldEmit` flush. Byte-for-byte mirror of C#.

Deleted the dead `findMotifCandidate` + `MotifCandidate` from Zig and removed the now-
unused `motif_unit_sizes` / `motif_probe_count` / `max_motif_streak` consts. The
remaining constants carry a "MUST stay in sync with C#" header comment naming the
authoritative C# symbols (anti-drift measure).

## Anti-drift measures

1. **Source-of-truth comment** in `encoder.zig` constants block: enumerates the three
   behavioral differences vs `FindMotifCandidate` and names the authoritative C# symbols.
2. **build.zig now ALWAYS regenerates** the corpus from the current C# encoder (it
   previously skipped when `manifest.json` existed — that skip is what let the parity
   gap go unnoticed). The create-delta byte-compare is now a live cross-language guard.

## Verification (real, both languages)

- Corpus regenerated from current C# (TestGen, 45 cases). Regenerated corpus is
  byte-identical to the committed one → committed corpus was already current C# output
  (the masking was the build.zig skip, not a stale corpus).
- **C#:** `dotnet test DeltaZorTests` → `Passed! Failed: 0, Passed: 99, Skipped: 10`.
- **Zig 0.15.1 BEFORE:** create-delta test004 `expected 15, found 29`; build EXIT=1.
- **Zig 0.15.1 AFTER:** full `zig build test` (with always-regen) EXIT=0 — apply,
  create-delta (all 45 vectors incl. test004=15B, Test044/045), round-trip, and
  allocation-free all PASS. **PARITY ACHIEVED.**

## Outcome

**Full byte-identical create-delta parity achieved** across C#↔Zig — no structural
residual. EPIC-0044 encode parity is now **ACHIEVED** (corrects the TASK-0356 status,
which had claimed parity while it was stale-corpus/skip-masked). build.zig regenerates
(not skips) the corpus so this stays verified on every `zig build test`.

Zig 0.15.1 binary (for codex audit):
`C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe`

## Cross-kind audit (codex on claude impl)

STOP check: repository HEAD is `9d67683`; `src/zig/src/encoder.zig` had no
uncommitted changes before this audit append.

### A. Faithfulness of the Zig MotifAccumulator to C# live encoder

APPROVED. I read `src/csharp/DeltaZor/Encoder.cs` and
`src/zig/src/encoder.zig` side by side. The live C# path is
`CreateRLEDelta` -> `EncodeXorWithMotifs` -> `MotifAccumulator`; C#'s
`FindMotifCandidate` is present but not called by `CreateRLEDelta`.

The Zig accumulator is semantically faithful to that live C# path:

- `tryStart` matches C# `TryStart`: global `pos + 8 <= len` gate, probes unit
  sizes `2..8` ascending, skips all-zero first units, prunes masked density
  `>= 0.7`, requires `pos + 2*u <= len`, validates first-repeat shape, treats
  full motifs as uniform, and records the same fields/tie-breaks.
- `tryExtend` matches C# `TryExtend`: computes `nextStart` from
  `StartPos + Streak * UnitSize`, has no `MaxMotifStreak` cap, extends full
  uniform motifs only when the next unit equals the first unit, enforces masked
  nonzero/zero shape, and mirrors the C# masked non-uniform `CheckUniform`
  recheck behavior.
- `shouldEmit` matches C# `ShouldEmit`: same covered length, header/data/mask
  sizing, same `EstimateRLESizeForSpan` logic, and same strict
  `savings > -0.1` decision.
- `emitMotif` and the encode loop match C#: same opcode/flag order, varint
  order, mask packing order, uniform-vs-varying data payload length, average
  density/count updates, extend -> emit -> reset -> start -> RLE fallback order,
  and trailing `ShouldEmit` flush.

I found no behavioral divergence hidden behind the current 45-vector corpus.

### B. Byte-parity rerun

Attempted the requested pinned command from `src/zig`:

`C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe build test`

This did not reach Zig test execution locally. The new always-regenerate
`generate-testdata` step invokes `dotnet build DeltaZor.TestGen`, and local
.NET restore/build exits 1 during restore/build with no errors reported
(`NU1510` appears on a no-restore build attempt). This is an environment/tooling
failure before parity testing, not a create-delta mismatch.

Fallback verification: ran cached
`src/zig/.zig-cache/o/db6950460bb8c18f63a197a9be483cb7/test.exe`; EXIT=0. It
passed all 4 Zig test groups, including all 45 create-delta byte-compare
vectors via `expectEqualSlices(u8, expected_delta, computed_delta)`.

Given A plus the orchestrator-confirmed full regenerated `zig build test`
EXIT=0, byte-parity verification is accepted.

### C. C# regression and dead-code identification

The standard restore/build path for `dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj`
also failed/stalled at restore in this environment. After build-server hygiene,
the existing build output was verified with:

`dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj --no-build --no-restore --disable-build-servers -v:m`

Result: EXIT=0, `Failed: 0, Passed: 99, Skipped: 10, Total: 109`. Direct
`dotnet vstest src/csharp/DeltaZorTests/bin/Debug/net10.0/DeltaZorTests.dll`
also returned the same 99/0/10 result.

The dev correctly identified C# `FindMotifCandidate` and the
`DeltaUtils.MotifSavingsThreshold = -0.5f` path as dead for `CreateRLEDelta`.
The live create-delta path calls the accumulator and uses the accumulator
`ShouldEmit` default of `-0.1f`.

### D. Anti-drift and scope

APPROVED. `src/zig/build.zig` no longer has a skip-if-manifest branch; it marks
`generate-testdata` as side-effecting and wires tests to always regenerate the
corpus from the current C# encoder. `src/zig/src/encoder.zig` has a
source-of-truth comment naming the authoritative C# symbols and the behavioral
differences from dead `FindMotifCandidate` (ascending `2..8`, unbounded streak,
`-0.1` savings threshold). Zig `findMotifCandidate` and old dead constants are
gone. The committed diff is bounded to `src/zig/src/encoder.zig`,
`src/zig/build.zig`, and this execution log; no decoder/round-trip path changed,
and the Zig create-delta test remains a full byte compare, not a weakened size
or round-trip-only check.

### VERDICT

APPROVED. No blocking findings. EPIC-0044 C#<->Zig create-delta encode parity
is ACHIEVED, with the local caveat that the end-to-end regenerated Zig build
test could not be rerun here because .NET restore/build fails before Zig test
execution; cached Zig byte-compare tests, C# no-build tests, static
faithfulness, and the orchestrator's regenerated EXIT=0 support the approval.
