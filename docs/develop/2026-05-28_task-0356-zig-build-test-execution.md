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
43/43 tests ran; "No leak" reported for every test (1..43).
```

**Result: C#↔Zig byte-identical parity is VERIFIED.** All 43 shared test vectors (the C# `DeltaZor.TestGen` corpus under `testdata/`) are byte-compared by `tests.zig` and pass on the Zig side, with no memory leaks. This satisfies TASK-0356 (the EPIC-0043 zig-side verification gate) on the project's target toolchain.

**Toolchain findings (for TASK-0371):**
- DeltaZor zig targets **0.15.x** — `build.zig` (addLibrary+createModule API) + `tests.zig` (`GeneralPurposeAllocator`, `std.fs` I/O) all compile + pass on 0.15.1.
- On **0.16.0**: `zig build` (engine library) compiles clean, but `zig build test` does not (GPA→DebugAllocator rename + the `std.fs`→`std.Io` overhaul ~12 sites). A deliberate 0.16 `std.Io` migration is a separate task; until then, **pin 0.15.x via `build.zig.zon` (TASK-0371)**.

TASK-0356 → review (parity confirmed on 0.15.1; codex independent re-run to follow).
