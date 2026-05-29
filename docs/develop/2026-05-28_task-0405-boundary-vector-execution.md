# TASK-0405 — Near-threshold boundary parity vectors (execution log)

- **impl-kind:** claude (Opus 4.8, no sandbox)
- **Date:** 2026-05-28
- **Epic:** EPIC-0044 (cross-language parity)
- **Working dir:** `C:/Users/austi/src/DeltaZor`
- **zig 0.15.1 binary (for codex re-verify):** `C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe`

## Objective

Add near-threshold boundary vector(s) to the shared C#↔Zig parity corpus so future
drift of the `compression_threshold = 1.5` FullReplace fallback fails through the
cross-language byte corpus (TASK-0359 aligned both languages to 1.5; existing corpus
max expansion ~1.255×, so the fallback path was never exercised).

## Corpus pipeline (tracked vs regenerated)

- TestGen: `src/csharp/DeltaZor.TestGen/` — `ITestCase` + `TestCases/*.cs` registered in
  `Program.cs`. Emits `testN.base/.next/.delta.bin`, `.md`, `manifest.json`, `checksums.txt`.
- **`testdata/` is gitignored** (`.gitignore` line `testdata/`). Nothing under `testdata/`
  is tracked. `build.zig`'s `generate-testdata` shells to `dotnet TestGen` **only if
  `testdata/manifest.json` is absent** (it skips regen when present).
- **Tracked artifacts for this task = the TestGen `.cs` files only** (Program.cs +
  3 new TestCases files). The corpus binaries/manifest are regenerated on demand.

## Boundary construction (the two new vectors)

Both: 512B base = `new Random(0xDE17A30).NextBytes(...)`; `next` = base XORed in
**alternating runs of length mostly-1 / occasionally-2** (`twoEveryN=4`) with per-byte
seeded **nonzero** XOR values (`ThresholdBoundaryBuilder.BuildNext`). Irregular short
runs defeat motif coalescing and maximise RLE overhead, letting the RLE-encoded delta
size be tuned to straddle `newLen × 1.5 = 768`. The two vectors use adjacent run-seeds
(`0xDE17A31` vs `0xDE17A32`) — a one-step change that nudges the C# RLE size ±1–2 bytes
across the strict `>` boundary. Seeds found empirically via a throwaway probe
(`DeltaZor.CreateDelta` with `CompressionThreshold=1000` to read raw RLE size).

| Test | seed | C# raw RLE data | vs 768 | C# decision | C# delta | type byte |
|------|------|-----------------|--------|-------------|----------|-----------|
| **Test044** ThresholdBoundary_BelowRLE | 0xDE17A31 | **767** | ≤ 768 | RLE kept | 772 B | `0x00` (RLE) |
| **Test045** ThresholdBoundary_AboveFullReplace | 0xDE17A32 | **769** | > 768 | FullReplace fallback | 517 B | `0x01` (FullReplace) |

Fallback condition (C# `DeltaZor.cs:208`): `dataSpan.Length > newData.Length * 1.5`
(strict `>`). 767 ≤ 768 keeps RLE; 769 > 768 triggers FullReplace. **In C# the fallback
boundary is genuinely exercised**, one vector each side — verified at the byte level by
inspecting `delta[4]` (compression-type) = 0x00 vs 0x01.

## Test results

### C# — GREEN
`dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj` → **Passed: 99, Failed: 0,
Skipped: 10**. `DeltaFileValidationTests.ValidateTestGenSamples` is a `[Theory]` over
every manifest entry (incl. test044/045): it recomputes the delta at threshold 1.5,
byte-compares vs the committed `.delta.bin`, and round-trips apply. Both new vectors pass.

### Zig 0.15.1 — RED (real parity divergence found — NOT papered)
`cd src/zig && <zig0151> build test` → **EXIT=1**. The `apply` and `round trip` passes
(which feed C#'s delta bytes to Zig's *decoder*) PASS for all 45 vectors incl. 44/45 —
**decode parity holds**. The `create delta` pass (Zig *encoder* vs C# `.delta.bin`,
`expectEqualSlices`) **fails at test004 (Mixed 1KB)** — `expected 15, found 29`.

This is a **pre-existing C#↔Zig encoder divergence**, confirmed independent of the new
vectors: regenerating the corpus WITHOUT test044/045 (temporarily commenting them out)
still fails identically at test004. Prior "PASS" reports (TASK-0356 commit 0671c7d,
TASK-0359) were against a **stale on-disk corpus** that `build.zig` never regenerated
(it skips regen when `manifest.json` exists) — so the byte-compare was comparing Zig's
output to a `.delta.bin` produced by an *older* C# encoder, not the current one.

Focused per-vector probe (throwaway `_probe_boundary.zig`, since the suite aborts at
test004 before reaching 44/45's create-delta) on the two new vectors:

| Test | C# RLE size / decision | Zig RLE size / decision | Result |
|------|------------------------|--------------------------|--------|
| Test044 | 767 → RLE (772 B) | **629** → RLE (629 B) | size differs; both RLE |
| Test045 | 769 → **FullReplace** (517 B, type 0x01) | **625** → **RLE** (625 B, type 0x00) | **opposite fallback decision** |

**Root cause (high-signal):** the C# and Zig RLE/motif encoders are NOT byte-identical
on irregular short-run / motif patterns — Zig coalesces more aggressively (smaller
output). The threshold *value* is aligned (both 1.5), but the **RLE size that feeds the
`> newLen×1.5` comparison diverges between languages**, so for Test045 the two languages
make **opposite FullReplace decisions** (C# falls back to FullReplace; Zig keeps RLE).
This is exactly the class of bug a boundary vector is meant to surface — the vector did
its job. It was masked previously by (a) the stale corpus and (b) existing vectors never
approaching the threshold.

## Disposition (no papering)

- Per the standing "parity is sacred / no papering" rule and the brief's explicit
  instruction ("if C# and Zig DISAGREE at the boundary, that's a REAL parity bug —
  report it, don't paper"), I did **not** tune the seeds into a region where the two
  encoders happen to agree (that would hide the divergence and defeat the test).
- The boundary vectors are correct and deterministic; they straddle the threshold in C#.
  They are committed as the deliverable. The Zig-side encoder divergence is a **separate,
  larger parity bug** (encoder RLE/motif coalescing must be reconciled C#↔Zig) that needs
  its own task — it is out of scope for "add a boundary vector" and would be a substantial
  encoder change. Investigation-only here is the rigorous call.

## Committed vs regenerated

- **Committed (tracked):** `src/csharp/DeltaZor.TestGen/Program.cs` (2-line registry add),
  `TestCases/Test044_ThresholdBoundary_BelowRLE.cs`,
  `TestCases/Test045_ThresholdBoundary_AboveFullReplace.cs`,
  `TestCases/ThresholdBoundaryBuilder.cs`, this exec log.
- **Regenerated (gitignored, NOT committed):** all of `testdata/` (both
  `DeltaZor.TestGen/bin/.../testdata` and `src/zig/testdata`).

## Handoff for codex re-verify

1. `dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj` → green (99/0/10).
2. Regenerate corpus + sync to zig (delete stale testdata first, else build.zig skips):
   build TestGen, `rm -rf bin/.../testdata && dotnet run --project DeltaZor.TestGen`,
   then `rm -rf src/zig/testdata && cp -r .../bin/Debug/net10.0/testdata/* src/zig/testdata/`.
3. `cd src/zig && C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe build test`
   → currently EXIT=1 at test004 (pre-existing encoder divergence) — reproduces the
   finding. The new vectors' apply+round-trip pass; their create-delta diverges as above.
4. Inspect `delta[4]` of `test044.delta.bin` (0x00 RLE) vs `test045.delta.bin` (0x01
   FullReplace) to confirm the C#-side straddle.

**Recommended follow-up task:** reconcile the C#↔Zig RLE/motif encoder so `create delta`
byte-compare is green across the full corpus (precondition for the boundary vectors to
serve as a clean threshold-drift detector).

## Cross-kind audit (codex on claude impl)

- **Date:** 2026-05-28
- **Auditor:** codex, independent audit of the TASK-0405 claude implementation and the surfaced C#<->Zig encoder-divergence finding.
- **Working dir:** `C:/Users/austi/src/DeltaZor`
- **Graph:** read-only inspection only; no graph writes.

### Guardrails

- STOP condition passed: `git rev-parse --short HEAD` -> `296ea34`.
- Initial worktree was clean. After test execution and before this append, `git status --short -- src/zig src/csharp/DeltaZor.TestGen` was still clean.
- No uncommitted `src/zig` or `DeltaZor.TestGen` source changes were present beyond this audit append.

### A. Boundary vectors correct + actually straddle

Confirmed.

- `Test044_ThresholdBoundary_BelowRLE`, `Test045_ThresholdBoundary_AboveFullReplace`, and `ThresholdBoundaryBuilder` are deterministic: shared 512B base seed `0xDE17A30`, adjacent run seeds `0xDE17A31` / `0xDE17A32`, and seeded nonzero XOR values over mostly-1/occasionally-2 alternating runs.
- `Program.cs` registers both vectors as Test044 and Test045; `DeltaFileValidationTests.ValidateTestGenSamples` regenerates the corpus, byte-compares `DeltaZor.CreateDelta(...)` against each generated `.delta.bin`, and round-trips apply.
- Byte-level measurements from the generated C# corpus:
  - Test044 normal delta: `delta_len=772`, `delta[4]=0x00`, RLE data length `767`.
  - Test045 normal delta: `delta_len=517`, `delta[4]=0x01`, FullReplace payload length `512`.
- Forced-RLE probe using the same C# test-case classes and `CompressionThreshold=1000.0`:
  - Test044 forced RLE: `forced_rle_delta_len=772`, raw RLE data `767`, type `0x00`.
  - Test045 forced RLE: `forced_rle_delta_len=774`, raw RLE data `769`, type `0x00`.

This genuinely brackets `newLen * 1.5 = 512 * 1.5 = 768` across the strict `>` fallback: `767 <= 768` keeps RLE, while `769 > 768` falls back to FullReplace. This is not a contrived header-only pass.

C# run notes:

- The exact `dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj` entrypoint stalled/returned nonzero in this Codex sandbox before reaching test output.
- Fallback runner against the same built test assembly completed successfully:
  - `vstest.console.exe src/csharp/DeltaZorTests/bin/Debug/net10.0/DeltaZorTests.dll /Framework:.NETCoreApp,Version=v10.0`
  - Result: `Test Run Successful. Total tests: 109; Passed: 99; Skipped: 10`.
  - The output shows both `DeltaFileValidationTests.ValidateTestGenSamples(id: "044", ...)` and `id: "045"` passed.

### B. Encoder divergence is real

Confirmed. This is a real encoder byte-disagreement, not a harness artifact.

- In this sandbox, the exact `zig.exe build test` entrypoint reached `build.zig`'s testdata pre-step and printed `Test data exists, skipping generation.`, then failed before test execution with Windows sandbox `Access is denied` while Zig's build runner tried to spawn the compiler subprocess.
- Direct execution of the existing Zig 0.15.1 test binary from `.zig-cache` exercised the same `src/zig/src/tests.zig` harness:
  - `apply` passed for every valid vector, including Test044 and Test045.
  - `round trip` passed for every valid vector, including Test044 and Test045.
  - `allocation free all` passed.
  - `create delta` failed at Test004: expected C# delta size `15`, Zig computed `29`.
  - Final result: `3 passed; 0 skipped; 1 failed`.
- Focused one-vector manifests, run through that same Zig test binary, reached the boundary vectors' create-delta path:
  - Test044: C# expected `772`, Zig computed `629`; apply and round-trip still passed.
  - Test045: C# expected `517`, Zig computed `625`; apply and round-trip still passed.

The Test045 focused run is the decisive boundary signal: C# emits FullReplace (`517`, type `0x01`), while Zig emits a larger-than-FullReplace-but-under-threshold RLE delta (`625`, necessarily type `0x00` in the Zig encoder path). That is an encoder decision mismatch on identical input bytes.

Code corroboration:

- C#'s active small-buffer motif path in `src/csharp/DeltaZor/Encoder.cs` uses the streaming `MotifAccumulator` (`TryStart` / `TryExtend` / `ShouldEmit` / `EmitMotif`).
- Zig's `src/zig/src/encoder.zig` directly probes `findMotifCandidate(...)` at each position and emits immediately when its savings check passes.
- The threshold value is aligned (`CompressionThreshold` / `compression_threshold` = `1.5`), but the RLE/motif coalescing decisions that feed the threshold are not byte-identical. That explains C#=15 vs Zig=29 at Test004 and the Test045 opposite fallback decision.

### C. Stale-corpus masking + TASK-0356 implication

Confirmed.

- `src/zig/build.zig` only regenerates/copies C# testdata when `testdata/manifest.json` is absent:
  - if the manifest exists, it prints `Test data exists, skipping generation.`
- The exact `zig.exe build test` attempt in this audit printed that skip message before the sandbox spawn failure, confirming the live build path still has the skip behavior.
- Therefore a pre-existing on-disk `src/zig/testdata/manifest.json` can cause the Zig byte-compare harness to compare against an old C#-generated corpus, not the current C# encoder output.
- The current regenerated corpus fails Zig create-delta byte parity immediately at Test004 and on the new boundary vectors, while decode/apply and Zig self round-trip still pass.

Critical implication: TASK-0356's 0.15.1 "C#<->Zig byte-identical parity is VERIFIED" record was overstated. It verified the Zig encoder against the stale corpus present on disk at the time. The corrected reading is:

- C# delta decode/apply parity holds for the regenerated corpus.
- Zig self round-trip holds.
- Current C#<->Zig encode byte-parity does **not** hold.
- EPIC-0044 cannot claim encode parity until TASK-0429 reconciles the encoders and the regenerated corpus is byte-green.

### D. No papering + disposition sound

Confirmed.

- The implementation did not tune the boundary seeds into an accidental C#<->Zig agreement zone.
- The Zig byte-compare test remains failing on regenerated/current corpus data; it was not disabled, weakened, or changed to round-trip-only.
- Read-only graph check found:
  - `RESEARCH-2026-05-28-deltazor-csharp-zig-encoder-divergence`, summarizing Test004 C#=15 vs Zig=29, stale-corpus masking, and Test045 C# FullReplace vs Zig RLE.
  - `TASK-0429`, backlog priority 2, titled to reconcile the C#<->Zig RLE/motif encoder so create-delta is byte-identical across the full corpus.
  - Relationships link TASK-0429, TASK-0405, and the research record.

Severity: this is a genuine product-parity defect. DeltaZor's dual-language "byte-identical create-delta" claim is false against the current C# encoder. It is larger than TASK-0405's vector addition and must be resolved before EPIC-0044 can be called complete.

### E. Committed vs regenerated

Confirmed.

- `git show --stat --name-status HEAD` shows only the intended tracked artifacts:
  - `docs/develop/2026-05-28_task-0405-boundary-vector-execution.md`
  - `src/csharp/DeltaZor.TestGen/Program.cs`
  - `src/csharp/DeltaZor.TestGen/TestCases/Test044_ThresholdBoundary_BelowRLE.cs`
  - `src/csharp/DeltaZor.TestGen/TestCases/Test045_ThresholdBoundary_AboveFullReplace.cs`
  - `src/csharp/DeltaZor.TestGen/TestCases/ThresholdBoundaryBuilder.cs`
- `.gitignore` contains `testdata/`, `.zig-cache/`, `zig-out/`, `bin/`, and `obj/`.
- `git ls-files src/zig/testdata src/csharp/DeltaZor.TestGen/bin src/csharp/DeltaZor.TestGen/obj src/zig/.zig-cache` returned no tracked files.
- No build junk or regenerated corpus binaries are tracked.

### VERDICT

**APPROVED for TASK-0405.** The boundary vectors are deterministic, byte-measured, and genuinely straddle the C# fallback threshold. The implementation correctly surfaced and preserved a real C#<->Zig encoder divergence instead of papering it over.

**EPIC-0044 parity is NOT achieved.** Decode/apply and Zig self round-trip pass, but current C#<->Zig create-delta byte parity fails. TASK-0429 is required before claiming cross-language encode parity.
