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
