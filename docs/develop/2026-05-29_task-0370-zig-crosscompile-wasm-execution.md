# TASK-0370 — Zig cross-compile matrix + WASM artifact upload (tag-driven)

EPIC-0048 · branch `task-0370-zig-crosscompile-wasm` · base `master` @ 6aa04bc · DEV lane (claude/opus, pinned Zig 0.15.1).

Pinned toolchain: `C:/Users/austi/AppData/Local/Temp/zig0151/zig-x86_64-windows-0.15.1/zig.exe` (`zig version` → `0.15.1`).

## Scope

1. Make `build.zig` cleanly cross-compile the DeltaZor Zig lib for a target matrix.
2. Produce a usable WASM artifact (not just a `.a` archive).
3. Add a tag/release-triggered CI workflow that runs the matrix and uploads per-target artifacts.

## Findings before changes

- `src/zig/src/{deltazor,encoder,decoder,utils}.zig` are pure-Zig + allocator-based — `grep` for `std.fs|std.os|std.process|std.posix|@cImport|std.io.getStd` returned **nothing** (EXIT 1). Fully portable; no OS syscalls to break cross-targets/wasm.
- The lib (`addLibrary`, default static linkage) already cross-compiled to `x86_64-linux` out of the box (`zig build -Dtarget=x86_64-linux` → EXIT 0) with NO build.zig change. Same for every native target.
- BUT a wasm `addLibrary` emits `zig-out/lib/libdeltazor.a` — a static archive, **not a usable `.wasm` module**. The public API also takes `std.mem.Allocator` + Zig slices, which a wasm host can't express across the boundary.

## WASM allocator / exports decision

Added `src/zig/src/wasm.zig`: a minimal C-ABI adapter exporting `dz_alloc`, `dz_free`, `dz_create_delta`, `dz_apply_delta` (flat pointer/length ABI; host owns buffer lifecycle).

- **Allocator: `std.heap.wasm_allocator`** — the canonical freestanding-wasm allocator in Zig 0.15 (backed by `memory.grow`), needs no OS, so the SAME wrapper builds for both `wasm32-freestanding` and `wasm32-wasi`. Rejected page_allocator (its wasm impl is wasm_allocator anyway) and fixed-buffer (unbounded input sizes).
- **Emitted as an executable reactor**, not a lib: `wasm.entry = .disabled` (no `_start`/`main`), `wasm.rdynamic = true` (surface the `export fn` symbols). Produces `zig-out/bin/deltazor.wasm`.
- wasm.zig is compiled ONLY into the wasm artifact (gated on `target.result.cpu.arch.isWasm()` in build.zig); native lib/test builds never import it, so the host-ABI choices CANNOT perturb C#↔Zig byte-parity (the corpus still exercises `deltazor.zig` directly via `tests.zig`).

`build.zig` change: after `b.installArtifact(lib)`, an `if (target.result.cpu.arch.isWasm())` block adds + installs the wasm executable. Native targets unaffected; lib build path unchanged.

## Per-target compile evidence (pinned 0.15.1, `-Doptimize=ReleaseSafe` native / `ReleaseSmall` wasm)

| target | EXIT | artifact (bytes) |
|---|---|---|
| x86_64-linux | 0 | libdeltazor.a (3942) |
| aarch64-linux | 0 | libdeltazor.a (4094) |
| x86_64-windows | 0 | deltazor.lib (990) |
| aarch64-macos | 0 | libdeltazor.a (2520) |
| x86_64-macos | 0 | libdeltazor.a (2392) |
| wasm32-freestanding | 0 | deltazor.wasm (16363) + libdeltazor.a |
| wasm32-wasi | 0 | deltazor.wasm (21099) + libdeltazor.a |

WASM sanity: `\0asm` magic header confirmed (`head -c4 | xxd` → `0061 736d`); all four exports present in the binary (`grep -a -o 'dz_[a-z_]*'` → `dz_alloc, dz_apply_delta, dz_create_delta, dz_free`). 16363-byte freestanding module — non-trivial, real reactor.

## Regression: native `zig build test`

`zig build test` (native, pinned 0.15.1) → **EXIT 0**, "Round trip test N passed" through **test 56** (all 56 vectors), no leaks. No regression from the build.zig change.

## CI workflow

`.github/workflows/zig-artifacts.yml`:
- **Trigger: `on: push: tags: ['v*']` ONLY** — mirrors `publish.yml` (TASK-0369). No `push`(branch)/`pull_request` triggers.
- Matrix over all 7 targets (5 native + 2 wasm), `fail-fast: false`.
- `mlugg/setup-zig@v2` pinned to `0.15.1`.
- `zig build -Dtarget=... -Doptimize=ReleaseSafe` in `src/zig` (no .NET needed — `build` doesn't regenerate the corpus, only `test` does).
- `actions/upload-artifact@v4` uploads `zig-out/lib/*` + `zig-out/bin/*` per target with `if-no-files-found: error`.
- `permissions: contents: read` (read-only; no package write needed for artifact upload).

**Why independent of TASK-0373:** the dual-default-branch ambiguity (TASK-0373) only affects branch `push`/`pull_request` CI gating (TASK-0372). A tag-triggered workflow fires on `refs/tags/v*` regardless of which branch is "default", so it is unaffected by that decision and safe to ship now. Writing this file does NOT run it — it only fires on a tag the USER pushes.

## Deferred

- Push/PR test-gating workflow (dotnet test + `zig build test`) → **TASK-0372**, blocked on **TASK-0373** (dual-default-branch). Not wired here.
- Release-asset attachment (vs. build artifacts) can be added if the user wants `.wasm` on the GitHub Release page; current workflow uploads as build artifacts, consistent with the task's "release/build artifacts" framing.

## Pre-commit hygiene

`.zig-cache/` and `zig-out/` removed before commit. Staged source-only with explicit `git add` (build.zig, src/wasm.zig, workflow, this exec log). No build artifacts, no testdata regen committed. NOT pushed.

## Cross-kind audit (codex on claude impl)

### A. build.zig cross-compile + wasm gating

APPROVED. `src/zig/build.zig` still uses `b.standardTargetOptions(.{})` and the existing static lib install remains the default non-wasm build path. The new reactor artifact is gated solely by `if (target.result.cpu.arch.isWasm())`, so native targets do not import `src/wasm.zig` or install the executable artifact. The test/corpus path remains unchanged: `generate-testdata` is only a dependency of the `test` step through `run_tests`, not the default `zig build` install path.

### B. wasm.zig C-ABI correctness

APPROVED. `src/zig/src/wasm.zig` is a thin adapter over `DeltaZor.createDelta` and `DeltaZor.applyDelta`; it does not reimplement codec logic. `dz_alloc` and `dz_free` both use `std.heap.wasm_allocator`, and the ABI carries the slice length required for the caller to free buffers correctly. `dz_create_delta` returns a fresh pointer and writes the delta length through `out_len`, which gives JS/wasm hosts the missing result length needed to copy/free the returned buffer. `dz_apply_delta` accepts caller-provided output ptr/len and forwards to the native contract; the delta format itself carries decoded output length and the decoder rejects undersized output. No double-free, use-after-free, missing export, or wasm32 integer-truncation issue found in the adapter surface.

### C. Workflow correctness

APPROVED. `.github/workflows/zig-artifacts.yml` triggers only on `push` tags matching `v*`, with no branch `push` or `pull_request` trigger. The matrix contains all 7 targets: x86_64-linux, aarch64-linux, x86_64-windows, aarch64-macos, x86_64-macos, wasm32-wasi, and wasm32-freestanding. Zig is pinned to 0.15.1 via `mlugg/setup-zig@v2`, artifacts are uploaded with `actions/upload-artifact@v4`, the upload paths match `src/zig/zig-out/lib/*` and `src/zig/zig-out/bin/*`, and no hardcoded secrets are present.

### D. Byte-parity + scope

APPROVED. The parity tests continue to import and exercise `deltazor.zig` directly, while `wasm.zig` is only compiled for wasm reactor artifacts. The diff is bounded to the expected 4 files: build.zig, wasm.zig, the tag-only workflow, and this execution log. No codec core files were modified, so C# <-> Zig byte-parity remains untouched by this task.

### VERDICT

APPROVED. The cross-compile matrix and wasm C-ABI are statically correct and memory-safe under the documented host contract; the workflow is tag-only and independent of TASK-0373; byte-parity is untouched. Orchestrator may merge `task-0370-zig-crosscompile-wasm` to `master` and close TASK-0370.
