# TASK-0371 — Add build.zig.zon for Zig package consumption — execution log

- **Task:** TASK-0371 (EPIC-0048, CI / dual-language packaging — Zig side)
- **impl-kind:** claude (opus)
- **Branch:** `task-0371-build-zig-zon` (from master @ f4428ea)
- **Toolchain:** zig 0.15.1 (pinned `…/zig0151/zig-x86_64-windows-0.15.1/zig.exe`)
- **Scope:** add `src/zig/build.zig.zon` only. No build.zig change required; encoder/decoder logic untouched.

## Package identity

| Field | Value |
|---|---|
| `.name` | `.deltazor` (enum literal — 0.15.1 schema) |
| `.version` | `"1.0.0"` (matches the lib `.version = {1,0,0}` set in build.zig; no shared C# `<Version>` exists, so the build.zig lib version is the source of truth) |
| `.minimum_zig_version` | `"0.15.1"` |
| `.fingerprint` | `0x1dfb81dccb0dc7db` |
| `.dependencies` | `.{}` (no external Zig deps — confirmed) |
| `.paths` | `build.zig`, `build.zig.zon`, `src` |

Public root module a consumer imports: `src/deltazor.zig`, exposing
`DeltaZor.createDelta` / `DeltaZor.applyDelta` (`deltazor.zig` resolves its
`encoder.zig` / `decoder.zig` / `utils.zig` siblings via relative `@import`, so
shipping the whole `src/` dir is sufficient and self-contained).

## 0.15.1 .zon schema specifics handled

- **`.name` is an enum literal** in 0.15.1 (`.deltazor`), NOT a string. Verified
  against `zig init` output from the pinned 0.15.1 binary.
- **`.fingerprint` is mandatory.** Verified empirically: building with the field
  omitted produced
  `error: missing top-level 'fingerprint' field; suggested value: 0x1dfb81dccb0dc7db`.
  Used the toolchain-reported value (NOT guessed, NOT copied from another package).
  This is the package's permanent globally-unique id.
- **`.paths`** lists the build root files a consumer/`zig fetch` needs. `src` is a
  directory entry (recursive). `testdata/` and `.zig-cache/` are excluded (test-only
  / build artifacts, both gitignored). `tests.zig` / `gen_testdata.zig` live under
  `src/` and are included with the dir per zig convention (harmless, small).

## Verification evidence (zig 0.15.1, with .zon present)

Cache + testdata cleaned (`rm -rf src/zig/.zig-cache src/zig/testdata`) before each run.

- **Baseline (no .zon):** `zig build` → exit 0.
- **`.zon` missing fingerprint:** `zig build` →
  `error: missing top-level 'fingerprint' field; suggested value: 0x1dfb81dccb0dc7db`
  (confirms the .zon is parsed and validated by zig).
- **`zig build` (final, .zon present):** exit 0, 0 errors. .zon parses clean.
- **`zig build test` (final, .zon present):** exit 0. Testdata regenerated from the
  current C# encoder; all 56 vectors (tests 1–56) pass with "No leak" for every test.
  No regression.

## Notes for codex audit

Codex cannot spawn zig in its sandbox — re-audit is a static review of the .zon
(field names/types/format vs the 0.15.1 schema, fingerprint provenance, paths
correctness) plus this build+test evidence. Build/test were run for real on the
pinned 0.15.1 binary in the orchestrator shell.

## Cross-kind audit (codex on claude impl)

### A. `.zon` schema correctness for Zig 0.15.1

APPROVED. `src/zig/build.zig.zon` uses the expected 0.15.1 package manifest shape:

- `.name = .deltazor` is an enum literal, not a string.
- `.version = "1.0.0"` is valid semver.
- `.fingerprint = 0x1dfb81dccb0dc7db` is present as a u64-sized hex literal.
- `.minimum_zig_version = "0.15.1"` is a valid version string.
- `.dependencies = .{}` is an empty struct.
- `.paths` is a struct of string entries.

The orchestrator-confirmed `zig build` exit 0 with the `.zon` present corroborates that the field set parses under Zig 0.15.1.

### B. Package identity and paths

APPROVED. `.name = .deltazor` is a valid and sensible Zig identifier. `.version = "1.0.0"` matches `build.zig`'s library version (`.{ .major = 1, .minor = 0, .patch = 0 }`), and I found no separate shared version source that the Zig package should track.

`.paths = .{ "build.zig", "build.zig.zon", "src" }` includes the build root and all source needed by the public root `src/deltazor.zig`. The public root imports only sibling files (`encoder.zig`, `decoder.zig`, `utils.zig`) plus `std`; those transitive sources stay under `src/`.

The package path excludes non-source/generated directories such as `testdata/`, `.zig-cache/`, and `zig-out/`. `src/tests.zig` and `src/gen_testdata.zig` are included because the manifest ships `src/` recursively; they are not imported by the public root and do not pull fixture data into the package, so this is not a packaging blocker.

### C. Fingerprint provenance and external deps

APPROVED. The fingerprint value in the manifest matches the orchestrator log's Zig 0.15.1 missing-fingerprint diagnostic:
`suggested value: 0x1dfb81dccb0dc7db`. The manifest comment also flags the security/trust significance. I did not regenerate the value in this sandbox.

`dependencies = .{}` is consistent with the source: Zig files import only `std`, local sibling files, or the build-local test root. No external Zig package imports were found.

### D. Regression and scope

APPROVED. The implementation commit changes only `src/zig/build.zig.zon` and this execution log; `build.zig` and encoder/decoder logic are unchanged. The orchestrator-confirmed `zig build test` evidence remains the regression check for the 56 vectors.

Read-only graph check: `TASK-0371` is contained by `EPIC-0048`, implements the schema decision, and has a `REFERENCES_COMMIT` edge to `09a692e`. No graph writes were made.

### E. Independent check

APPROVED. I did not run Zig, per the toolchain note. The independent value-add here is static manifest review, path/import closure review, fingerprint-provenance corroboration from the execution log, commit-scope review, and read-only graph-edge verification.

### VERDICT

APPROVED. The `build.zig.zon` is valid for Zig 0.15.1 and correct for packaging DeltaZor's Zig library as a consumable package. Orchestrator may merge `task-0371-build-zig-zon` to `master` and close `TASK-0371`.
