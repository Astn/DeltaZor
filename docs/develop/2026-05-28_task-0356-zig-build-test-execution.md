# TASK-0356 — `zig build test` on Zig 0.16: execution log

- **Date:** 2026-05-28
- **Task:** TASK-0356 — Confirm `zig build test` passes on a Zig-equipped host (gates EPIC-0043)
- **Lane / impl-kind:** DEV (claude, no sandbox; build/test for real)
- **Working dir:** `C:/Users/austi/src/DeltaZor/src/zig`
- **Outcome:** **STOPPED at scope guard.** Verification is **blocked** beyond a clean mechanical rename. Tree reverted to clean. No source committed. Task → `backlog` pending a deliberate Zig version decision.

## Toolchain

- `zig version` → **0.16.0** (winget `zig.zig`, on PATH).
- DeltaZor's zig sources were written for ~zig 0.15.

## What compiles vs. what doesn't

- `zig build` (library / engine artifact: `build.zig` + `encoder.zig` / `decoder.zig` / `utils.zig` / `deltazor.zig`) — **compiles cleanly on 0.16** (exit 0, no output). Confirms the orchestrator's finding; the engine is 0.16-clean.
- `zig build test` (compiles `src/tests.zig`) — **FAILS to compile.** It never ran a single test. Two successive waves of 0.16 API breakage:

### Wave 1 — `GeneralPurposeAllocator` (the known one)

4 sites: `std.heap.GeneralPurposeAllocator(.{...}){}`. In 0.16 the GPA was renamed `std.heap.DebugAllocator` (the old alias, present in 0.15, was dropped). This **is** a pure mechanical rename — verified identical shape in 0.16 stdlib:
- `std/heap.zig:20` exports `pub const DebugAllocator`.
- `std/heap/debug_allocator.zig`: `Config` has `safety: bool` (line 124); `allocator()` (318); `deinit() std.heap.Check` (495) — so `_ = gpa.deinit()` and `if (gpa.deinit() == .leak)` both still typecheck.

Applied the rename to all 4 sites. Re-ran — Wave 1 cleared, Wave 2 surfaced.

### Wave 2 — `std.fs` → `std.Io` I/O overhaul (the cascade — the blocker)

4 new errors: `error: root source file struct 'fs' has no member named 'cwd'` on every `fs.cwd().openFile(...)` call. This is the 0.16 "Writergate" / `std.Io` I/O rework, NOT a rename:

- `std.fs.cwd()` is **removed**. `cwd()` now lives at `std.Io.Dir.cwd()` (`lib/std/Io/Dir.zig:88`). The entire `std.fs` Dir/File implementation was relocated under `std.Io` — `lib/std/fs/` contains no `.zig` files.
- `Dir.openFile` signature **changed**: `openFile(dir: Dir, io: Io, sub_path, options)` (`Io/Dir.zig:577`) — it now requires an explicit `io: Io` interface argument that did not exist in 0.15. The test calls it with 2 args.
- `File.close` **changed**: `close(file: File, io: Io)` (`Io/File.zig:221`) — now requires `io`. The test's `defer file.close()` breaks.
- `File.readToEndAlloc` is **gone entirely.** Reading a file to a slice is now done through the new buffered reader interface: `File.reader(file, io, buffer)` → `Io.Reader` (`Io/File.zig:566`). This is a behavioral change requiring caller-supplied buffers and the new `Io.Reader` draining API.

### Why this is a STOP, not a fix

Per the brief's SCOPE GUARD: a migration that "cascades into semantic/behavioral changes" or where behavior-preservation is uncertain → STOP, do not force it. Wave 2 is exactly that:

- It is **not** a set of clean mechanical renames. It requires acquiring/threading an `Io` instance through ~12 call sites (4× `openFile`, 4× `close`, 4× `readToEndAlloc`-equivalents) and rewriting the file-read pattern onto the new `Io.Reader` streaming interface with explicit buffers.
- That is a genuine `std.Io` migration with non-trivial behavioral surface (buffering, reader lifecycle, error sets), not a rename — and it is real engineering work that deserves its own deliberate task, not a guessed in-line port under a "make tests green" mandate.
- Forcing it risks subtly changing how the parity corpus is read/compared — and the whole point of these tests is **byte-identical C#↔Zig parity**. Quietly mutating the I/O path is unacceptable risk against that invariant.

## Parity status

**NOT verified.** The tests are the C#↔Zig byte-compare harness, but they never compiled on 0.16, so zero tests ran. No divergence was found and none was ruled out — the parity claim remains **unverified** (same state as before, but now with a concrete reason: tests are 0.16-incompatible at the I/O layer). The shared corpus (`testdata/manifest.json` + vectors) is present; `build.zig generate-testdata` correctly skipped regeneration.

## Disposition / recommendation

The project must make a deliberate toolchain decision before this gate can close. Two clean paths:

1. **Pin Zig 0.15 (TASK-0371) and verify there.** The sources were authored for 0.15; on 0.15 the only break is the GPA→DebugAllocator rename (and even that may not be needed on 0.15, where `GeneralPurposeAllocator` is the deprecated-but-present alias). Add `build.zig.zon` with `minimum_zig_version = "0.15.x"` and run the harness on a pinned 0.15 toolchain. **Lowest-risk path to actually verifying parity.** (Recommended; adding `build.zig.zon` is TASK-0371's scope — flagged, not done here.)
2. **Deliberate 0.16 migration as its own task.** Port `tests.zig` (and verify the engine doesn't also rely on any soon-to-change `std.Io` surface) onto the 0.16 `std.Io` reader/writer model, then pin `minimum_zig_version = "0.16.0"`. Larger, behavior-sensitive — must be done carefully to preserve the byte-compare, and re-verified.

Either way: once a toolchain is pinned and the harness compiles, re-run `zig build test` to actually verify parity (the real headline that this task was meant to deliver).

## State left behind

- `src/tests.zig` — **reverted to clean** (`git checkout -- src/tests.zig`; `git diff --stat` empty). The DebugAllocator rename was applied then reverted along with everything else, since landing only Wave 1 leaves the build red anyway.
- **No commit made** (working tree clean; nothing to commit — would have been an empty/no-op commit, which the discipline forbids).
- Graph: TASK-0356 → `backlog` with STOP reason.

## Handoff packet

- **For codex (cross-kind audit):** codex CAN re-run `zig build test` on this 0.16.0 box and will reproduce the two-wave failure verbatim (GPA rename, then `fs.cwd`/`openFile`/`readToEndAlloc` cascade). `zig build` (library) succeeds. Tree is clean — no changes to audit; audit the STOP decision + this analysis.
- **Re-verify commands:** `zig version` → 0.16.0; `zig build` → ok; `zig build test` → 4 errors (GPA) → after DebugAllocator rename, 4 errors (`fs.cwd`).
- **Next action owner:** decide path 1 vs 2 above (TASK-0371 covers the pin).
- **Confidence:** High. Errors are deterministic and reproduced directly from the installed 0.16 stdlib; the `std.Io` signatures were read from the actual toolchain (`Io/Dir.zig`, `Io/File.zig`, `heap/debug_allocator.zig`).

---

## Orchestrator verification on zig 0.15.1 (the project's target toolchain) — PARITY CONFIRMED

The dev correctly STOPPED on zig 0.16 (the `std.Io` overhaul is a real behavioral migration, not a clean rename — would risk silently mutating how the parity corpus is read). Per the dev's recommendation, the orchestrator verified on the project's actual target, **zig 0.15.1** (downloaded from ziglang.org to `C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe`), with **NO source changes** (clean tree):

```
zig build test   →  EXIT=0
4 zig test functions pass; they iterate 42 VALID vectors from the 43-entry corpus (manifest entry 8 is intentionally invalid/skipped — TASK-0403 codex condition). "No leak" reported for every vector.
```

**Result: C#↔Zig byte-identical parity is VERIFIED.** The 42 valid shared test vectors (the C# `DeltaZor.TestGen` corpus under `testdata/`; entry 8 intentionally invalid/skipped) are byte-compared by `tests.zig` (`expectEqualSlices` create-delta + round-trip) and pass on the Zig side, with no memory leaks. This satisfies TASK-0356 (the EPIC-0043 zig-side verification gate) on the project's target toolchain.

**Toolchain findings (for TASK-0371):**
- DeltaZor zig targets **0.15.x** — `build.zig` (addLibrary+createModule API) + `tests.zig` (`GeneralPurposeAllocator`, `std.fs` I/O) all compile + pass on 0.15.1.
- On **0.16.0**: `zig build` (engine library) compiles clean, but `zig build test` does not (GPA→DebugAllocator rename + the `std.fs`→`std.Io` overhaul ~12 sites). A deliberate 0.16 `std.Io` migration is a separate task; until then, **pin 0.15.x via `build.zig.zon` (TASK-0371)**.

TASK-0356 → review (parity confirmed on 0.15.1; codex independent re-run to follow).

---

## Cross-kind audit (codex on claude/orchestrator)

- **Date:** 2026-05-28
- **Auditor:** codex, independent cross-kind audit of claude DEV STOP plus orchestrator 0.15.1 verification.
- **Working dir:** `C:/Users/austi/src/DeltaZor/src/zig`
- **Graph:** not updated.

### Guardrails

- HEAD guard passed: `git rev-parse --short HEAD` -> `0671c7d`.
- Recent history shows the expected TASK-0356 doc-only commits:
  - `0671c7d docs(deltazor): TASK-0356 - zig 0.15.1 build test PASSES (C#<->Zig parity verified)`
  - `e5cb8b8 docs(deltazor): TASK-0356 - STOP, zig 0.16 tests blocked beyond GPA rename (std.Io cascade)`
- Pre-append worktree guard passed: `git status --short` was clean, and `git status --short -- src/zig` was clean.
- `git show --name-only` confirms both `0671c7d` and `e5cb8b8` touched only this exec-log document, not Zig sources.

### A. 0.15.1 parity rerun

- The exact requested command was attempted with the extracted 0.15.1 compiler:
  - `C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe build test`
  - Result in this Codex sandbox: `EXIT=1`, blocked before compiling the test binary by `error.Unexpected: GetLastError(5): Access is denied` in `std/process/Child.zig` while Zig's build runner tried to spawn the compiler subprocess.
  - Re-running with `--global-cache-dir` inside the repo removed the global-cache permission issue but hit the same Windows async-pipe spawn guard. This appears to be a sandbox subprocess restriction, not a DeltaZor source failure.
- Direct test execution evidence:
  - Executed the 0.15.1 test executable from `.zig-cache/o/6d755766c2b482277142c6a30514526a/test.exe` in `src/zig`.
  - Result: `EXIT=0`; output ended with `All 4 tests passed.`
  - The four Zig test declarations ran: `apply`, `create delta`, `round trip`, and `allocation free all`.
  - The loops exercised valid manifest entries 1-7 and 9-43. Static manifest count: `totalTests=43`, manifest entries=43, `.delta.bin` files=43, valid entries=42, invalid/skipped id=8 (`isValid=false`).
- Audit correction: the prior "43/43" wording is imprecise if read as vector executions. The harness executes 42 valid vectors, because entry 8 is explicitly invalid and skipped by `if (!entry.isValid) continue;`. The approximate corpus-size claim remains true, but the precise parity wording should be "all 42 valid vectors from the 43-entry C# corpus."

### B. Parity test is real, not hollow

Confirmed. `src/tests.zig` is a real byte-parity harness:

- Reads `testdata/manifest.json`, which is produced/copied by `build.zig` from the C# `DeltaZor.TestGen` corpus when missing.
- For each valid manifest entry, reads `testdata/{baseFile}`, `testdata/{nextFile}`, and `testdata/{deltaFile}`.
- Verifies base, next, and expected-delta SHA-256 values with `testing.expectEqualStrings`.
- `apply` test feeds the C# `.delta.bin` bytes to `DeltaZor.applyDelta(...)` and asserts `testing.expectEqualSlices(u8, output, next_bytes)`.
- `create delta` test runs `DeltaZor.createDelta(...)`, asserts the computed delta length equals `entry.deltaSize`, then asserts `testing.expectEqualSlices(u8, expected_delta, computed_delta)`.
- `round trip` test creates a Zig delta, applies it, and asserts the reconstructed output byte-equals `next_bytes`.
- `allocation free all` is the leak check; the "No leak" lines are not the parity assertion. The byte-equality assertions above are the parity gate.

Conclusion: `EXIT=0` from this test binary means the Zig implementation byte-matches the C# corpus for every valid vector it exercises; it is not merely a no-crash/no-leak check.

### C. 0.16 STOP justification

Confirmed.

- Installed PATH Zig is `0.16.0`.
- Direct 0.16 compile probe on current sources with local cache dirs reproduced wave 1: four `std.heap.GeneralPurposeAllocator` errors in `src/tests.zig`.
- Local 0.16 stdlib inspection confirms the dev's wave 2 analysis:
  - `std/heap.zig` exports `DebugAllocator`; `GeneralPurposeAllocator` is absent.
  - `std/Io/Dir.zig` has `pub fn cwd() Dir` and `pub fn openFile(dir: Dir, io: Io, sub_path: []const u8, options: OpenFileOptions)`.
  - `std/Io/File.zig` has `pub fn close(file: File, io: Io) void` and `pub fn reader(file: File, io: Io, buffer: []u8) Reader`.
  - `readToEndAlloc` was not found on the 0.16 file API surface inspected.
  - `lib/std/fs/` no longer contains the old Dir/File implementation shape used by these tests.

The STOP was the right call. The GPA rename is mechanical, but the follow-on file I/O migration requires threading `Io`, changing close/read patterns, and choosing buffer/reader behavior. That is behavior-sensitive in a parity harness and should not be forced inline just to make tests green.

### D. Clean tree / no papering

Confirmed before this audit append:

- `git status --short` clean.
- `git status --short -- src/zig` clean.
- `git diff -- src/zig` empty.
- The two TASK-0356 commits are exec-log/doc-only commits.

No Zig source was modified to make tests pass. Any cache artifacts touched during audit were ignored build/cache outputs, not source changes.

### E. TASK-0371 recommendation

Confirmed sound.

- DeltaZor's current Zig source and test harness are authored against the 0.15-era APIs and pass under the project's target toolchain per orchestrator's no-sandbox 0.15.1 run, with this audit confirming the harness is a real byte-compare.
- Pinning the supported 0.15.x line in `build.zig.zon` is the lowest-risk way to make the verification gate reproducible.
- A 0.16 `std.Io` migration should be a separate task with deliberate review, because it changes the parity harness I/O path.

### Findings

- No blocking finding against the C#<->Zig parity headline for the valid corpus.
- Condition 1: this Codex sandbox could not independently complete the exact `zig build test` command because Zig subprocess creation is blocked here. I did execute the 0.15.1 test binary successfully and audited the harness statically.
- Condition 2: adjust wording from "43/43 vectors ran" to "42 valid vectors from a 43-entry corpus ran"; manifest entry 8 is invalid and intentionally skipped.

**VERDICT: APPROVED-WITH-CONDITIONS**

---

## CORRECTION (2026-05-28, via TASK-0405 / TASK-0430)

The "C#↔Zig byte-identical parity is VERIFIED" conclusion above was **OVERSTATED**. It verified the Zig encoder against the **stale on-disk corpus** present at the time — `build.zig`'s generate-testdata step **skips regeneration when `testdata/manifest.json` already exists**, so the Zig byte-compare was running against an old C#-generated corpus that happened to match, masking a live encoder divergence.

**Corrected reading (confirmed by TASK-0405 impl + TASK-0430 codex audit, both re-running on a regenerated corpus):**
- ✅ Decode/apply parity + Zig self round-trip: **hold** (all vectors).
- ❌ C#↔Zig **encode** (create-delta) **byte-parity: does NOT hold** — the encoders make different RLE/motif/fallback decisions (e.g. Test004 C#=15 vs Zig=29; boundary Test045 C# FullReplace(517) vs Zig RLE(625)).

**Implications:**
- **EPIC-0043 (Zig implementation) stays `done`** — the port + decode + round-trip are real and verified.
- **EPIC-0044 (cross-language parity) is NOT achieved on encode** — tracked by **TASK-0429** (reconcile the C#↔Zig RLE/motif encoder so create-delta is byte-identical across the full corpus) + `RESEARCH-2026-05-28-deltazor-csharp-zig-encoder-divergence`.
- The stale-corpus masking is itself a process gap (the regen-skip) — reconciling encoders should also ensure the corpus is regenerated (not skipped) in verification.
