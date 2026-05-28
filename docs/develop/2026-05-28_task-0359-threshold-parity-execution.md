# TASK-0359 — Reconcile C#↔Zig compression-threshold parity hazard: execution log

- **Date:** 2026-05-28
- **Task:** TASK-0359 (EPIC-0044, cross-language parity) — make the Zig encoder honor its
  `compression_threshold` config and align the default to C#'s `1.5`, removing the dead/misleading
  `0.95`.
- **Lane / impl-kind:** DEV (claude, no sandbox; build/test for real). Cross-kind codex audit to follow.
- **Working dir:** `C:/Users/austi/src/DeltaZor`
- **Outcome:** **DONE.** Parity-hardening landed; behavior preserved (both toolchains green). Task → `review`.

## The hazard (confirmed)

The RLE→FullReplace fallback fires when the RLE-encoded delta exceeds `newLen × threshold`.

- **C#** (`src/csharp/DeltaZor/DeltaZor.cs`): configurable + correct. `CompressionThreshold = 1.5`
  (line 53), used at line 208: `if (usedRLE && dataSpan.Length > newData.Length * options.CompressionThreshold)`
  — **double** arithmetic, honors the config.
- **Zig** (`src/zig/src/encoder.zig:203`, before): `if (used_rle and rle_data_len > new_data.len * 3 / 2)`
  — **HARDCODED** `* 3 / 2` (=1.5), integer arithmetic, **ignored** its own `Options.compression_threshold`.
- **Zig** (`src/zig/src/utils.zig:70`, before): `compression_threshold: f64 = 0.95` — **DEAD** (never read by the
  encoder) and **misleading** (≠ C#'s effective 1.5). If anyone wired it in, parity would silently break
  (0.95 vs 1.5).

Net before: both behaved as 1.5 → parity held (TASK-0356 verified 42/42), but a latent divergence sat in the
dead config. C# side was already clean — no C# change needed (confirmed: the only other `0.95` in DeltaZor.cs
is the line-185 `obviousFullReplace` density pre-check, unrelated to the fallback threshold).

## Before / after threshold state

| | C# | Zig (config default) | Zig (effective comparison) |
|---|---|---|---|
| **Before** | `1.5` (honored) | `0.95` (DEAD — never read) | hardcoded `* 3 / 2` (=1.5, integer) |
| **After**  | `1.5` (honored, unchanged) | `1.5` (now read) | `> newLen * options.compression_threshold` (=1.5, **double**, honors config) |

## The fix (2 Zig files, 0 C# files)

1. **`src/zig/src/utils.zig:70`** — `compression_threshold: f64 = 0.95` → `= 1.5`. Aligns the default to C# and
   removes the stale never-wired value (greenfield: replaced, no misleading leftover).
2. **`src/zig/src/encoder.zig:203`** — replaced the hardcoded integer `rle_data_len > new_data.len * 3 / 2`
   with double arithmetic that honors the config and exactly mirrors C#:
   ```zig
   if (used_rle and @as(f64, @floatFromInt(rle_data_len)) > @as(f64, @floatFromInt(new_data.len)) * options.compression_threshold) {
   ```
   `createDeltaWithStats` already has `options: utils.Options` in scope (signature line 172), so no threading was
   required. Switching integer→double matches C#'s `> newData.Length * 1.5` (double) exactly and makes a
   configurable non-.5 threshold unambiguous. Note: for the default 1.5 with integer `rleLen`/`newLen`, the old
   `new_data.len * 3 / 2` and the new float compare give the identical boundary — which is why the corpus stays
   byte-identical (the proof below).

## Verification (both real, both green)

### Zig — target toolchain 0.15.1 (PATH zig is 0.16, can't run the harness — used the pinned 0.15.1 binary)

```
cd C:/Users/austi/src/DeltaZor/src/zig
C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe build test   →  EXIT=0
```

All **42 valid vectors** from the 43-entry C# `DeltaZor.TestGen` corpus pass (entry 8 is intentionally
`isValid=false` and skipped — per TASK-0356/TASK-0403). All four test declarations pass: `apply`, `create delta`
(byte-compare `expectEqualSlices(expected_delta, computed_delta)`), `round trip`, `allocation free all`
(no leaks). **Parity PRESERVED** — the Zig-produced deltas remain byte-identical to the C# corpus.

### C# — `dotnet test`

```
dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj   →  EXIT=0
Passed!  Failed: 0, Passed: 97, Skipped: 10, Total: 107
```

The 10 skips are pre-existing (unimplemented arithmetic / checksum-corruption features), unrelated to this
change. C# source was not touched.

### Behavior-preserving?

**Yes.** Both implementations were already effectively 1.5, so the corpus is byte-identical on both sides after
the change. Green-on-green is the proof: this is a safe parity-*hardening* (config now honored + aligned), not a
behavior change. The dead 0.95 was removed without altering any emitted bytes.

## Boundary-vector follow-up (note, not done — not in scope)

The 43-entry corpus does **not** appear to exercise a vector right at the fallback boundary
(`rleLen ≈ newLen × 1.5`, which is what flips RLE→FullReplace). E.g. the largest RLE-expansion vector observed is
test 10 (Uniform Int32 1M): delta 5,263,518 vs next 4,194,304 ≈ **1.255×** — well under 1.5, RLE kept; no vector
appears to actually trigger the FullReplace fallback near the boundary. A purpose-built boundary vector (one with
`rleLen` just below 1.5× and one just above) would lock the threshold value into the cross-language byte-compare
and catch any future C#↔Zig threshold divergence directly. **Recommend a follow-up task** to add such a pair to
`DeltaZor.TestGen`. Not over-engineered here — the core reconciliation + parity-preserved verification is the
deliverable.

## State / commit

- Files changed: `src/zig/src/encoder.zig`, `src/zig/src/utils.zig` (Zig only; C# clean).
- Commit on `master` (not pushed): `fix(deltazor): TASK-0359 — Zig encoder honors compression_threshold config, aligned to C# 1.5 (parity)`.
- Graph: TASK-0359 → `review`.

## Handoff packet

- **For codex (cross-kind audit):** the change is 2 lines of Zig. Re-verify the byte-parity on the project's
  target toolchain.
  - 0.15.1 binary: `C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe`
  - Command: `cd src/zig && <0.15.1-zig> build test` → expect `EXIT=0`, 42 valid vectors pass.
  - If codex's sandbox blocks Zig subprocess spawn (as on TASK-0356), run the cached test binary directly from
    `src/zig/.zig-cache/o/<hash>/test.exe` → expect `EXIT=0`, `All 4 tests passed.`
  - C#: `dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj` → 97 passed, 0 failed.
  - Audit the float-vs-integer equivalence claim (default 1.5, integer operands → identical boundary) and confirm
    no other hardcoded threshold survives in either language.
- **Confidence:** High. Both test suites re-run for real on this no-sandbox host; the fix mirrors C#'s exact
  double comparison and the dead config is gone. The only judgment call (integer→double) is byte-equivalent for
  the default and strictly more correct for configurable thresholds.
