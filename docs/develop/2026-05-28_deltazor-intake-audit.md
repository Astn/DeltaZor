# DeltaZor — Project Intake Audit

**Date:** 2026-05-28
**Auditor:** Claude intake-audit sub-agent (read-only)
**Repo:** `C:/Users/austi/src/DeltaZor` · remote `git@github.com:Astn/DeltaZor.git`
**HEAD:** `d88639d` (master, clean working tree)
**Scope:** Establish ground-truth state of both language implementations, position in epic journey, branch model, and CI/packaging gap, to onboard DeltaZor into the project knowledge graph.

> **Method note (no-papering):** every state claim below is backed by a file path + line, a command I ran, or a commit SHA. Where I could not verify (Zig test execution — `zig` is not installed on this machine), it is called out as an explicit open question.

---

## Executive summary — where is DeltaZor in its journey?

DeltaZor is a **mid-life, dual-language library with a far more mature Zig implementation than prior reconnaissance suggested.** The core delta engine (RLE+XOR, 7-bit varint, SIMD, hybrid/full-replace fallback, unified header, XxHash32 checksum) and the MOTIF repeat opcodes (uniform 0x04 + varying 0x05, mask-based, allocation-free) are **fully implemented and byte-for-byte mirrored in BOTH C# and Zig** — including a genuine cross-language test-vector harness where Zig consumes C#-generated vectors and asserts byte-identical deltas. The project is NOT pre-parity; it is roughly at its self-declared **v0.5** milestone. What remains is genuinely backlog: the advanced compression modes (Float/Half 0x06–0x07, Channel 0x08, Arithmetic 0x09, Planar 0x0A) are **reserved-but-unimplemented on both sides** (their C# tests are `[SKIP]`), documentation lags reality (CRC32→XxHash32, Channel claimed "complete" but isn't), there is **no CI/packaging at all**, and there is **branch + repo hygiene debt** (a stale `origin/main`, gitignored Zig dir confusion, a misplaced duplicate source file). The single biggest correction to prior recon: **Zig is real and near-parity on shipped features, not "essentially testdata only."**

---

## C# state

**Projects (4, in `src/csharp/DeltaZor.sln`):**
| Project | Role | TFM | Evidence |
|---|---|---|---|
| `DeltaZor` | Core library (`DeltaZor.cs`, `Encoder.cs`, `Decoder.cs`, `Utils.cs`) | `net10.0` | `DeltaZor.csproj:4`; namespace `DZ` |
| `DeltaZor.Shared` | Test-vector manifest types (`ManifestEntry.cs`, `TestDataManifest.cs`) | `net10.0` | sln |
| `DeltaZor.TestGen` | Generates the shared test-vector corpus (43 cases) | `net10.0` | `Program.cs`, `TestCases/*` |
| `DeltaZorTests` | xUnit unit tests + BenchmarkDotNet | `net10.0` | sln |

**Build/test (independently re-run 2026-05-28):**
- `dotnet build DeltaZor.sln` → **0 errors, 315 warnings** (warnings are CS1591 missing-XML-docs in `TestGen` + nullability/xUnit-analyzer noise in tests/benchmarks — not core defects).
- `dotnet test DeltaZorTests` → **97 passed, 0 failed, 10 skipped** (107 total, 521 ms). This exactly matches the claim in `docs/develop/2026-03-16_dotnet10-production-readiness.md`.
- The **10 skipped** tests are the unimplemented advanced modes: `ArithmeticCompressionTests` (Global/Planar/PerRun/Clamp/AutoMode/RunArithmetic) plus a couple of checksum/7-bit edge cases — confirming those features are stubs, not shipped.

**Implemented opcodes (verified in `Encoder.cs` emit + `Decoder.cs` switch):**
- 0x00 ZeroRun, 0x01 NonZeroRun, 0x02 Extension, 0x03 Truncation — core, complete.
- 0x04 UniformMotifRepeat, 0x05 VaryingMotifRepeat — complete (mask-based, `MotifAccumulator`, `FindMotifCandidate`, lazy single-pass; `Encoder.cs:20-424`). Decoder rejects any opcode > 0x05 (`Decoder.cs:158-159`).

**Reserved-but-NOT-implemented (constants/structs only, never wired into encode/decode):**
- 0x06 FloatRun, 0x07 HalfRun, 0x08 ChannelRun, 0x09 Arithmetic, 0x0A Planar — declared in `Utils.cs:50-66`. `ChannelPattern` struct exists (`Utils.cs:107-116`) but `AnalyzeChannelPattern` is **not present** and the opcode is **never emitted or decoded** (grep: no `RLE_ChannelRun` usage in `Encoder.cs`/`Decoder.cs`). `OpCodeCounts` marks Float/Half/Channel `// (Planned)` (`DeltaZor.cs:121-123`).

**Checksum reality:** XxHash32, not CRC32. `DeltaZor.cs:218,327` call `XxHash32Wrapper.Compute`; `Utils.cs:342-348` wraps `System.IO.Hashing.XxHash32.HashToUInt32`; package ref `System.IO.Hashing 9.0.4` (`DeltaZor.csproj:14`). **No CRC32 references remain anywhere under `src/`** (grep clean) — only the docs lag (see Drift).

**Public API surface (`DeltaZor` static class):** `CreateDelta` (4 overloads: alloc + span, with/without options), `ApplyDelta`, `AnalyzeDelta`; types `DeltaOptions`, `DeltaStats`, `OpCodeCounts`, `DeltaResult<T>`. Header: `[output_length:4][compression_type:1][data...][checksum:4?]`, checksum presence self-described by bit 7 of the type byte (`DeltaZor.cs:13-38, 230-234`).

---

## Zig state (the crux — prior recon was wrong)

**The real Zig source is git-tracked at `src/zig/`, NOT the top-level `zig/`.** `git ls-files zig/` returns **0 files** — the entire top-level `zig/` directory (including the `zig/testdata/*.png` the orchestrator's `find` saw) is **gitignored** via the `testdata/` rule in `.gitignore:9`. The seven tracked Zig files are:

```
src/zig/build.zig
src/zig/src/deltazor.zig   (public facade: createDelta / applyDelta)
src/zig/src/encoder.zig    (379 lines — full encoder + motif detection)
src/zig/src/decoder.zig    (213 lines — full decoder, all 6 opcodes)
src/zig/src/utils.zig      (xxhash32, varint, opcode constants, Options/Stats)
src/zig/src/tests.zig      (cross-language parity test harness)
src/zig/src/gen_testdata.zig
```

**This is a real, near-complete port, not a skeleton.** `encoder.zig` mirrors the C# encoder: `createDeltaWithStats`, `createRLEDeltaDirect`, `encodeXorWithMotifsDirect`, `findMotifCandidate`, `checkUniform`, identical motif unit-size probe table `{4,8,2,3,5,6,7}` (`encoder.zig:18` vs C# `Utils.cs:70`), identical thresholds, identical header write (`encoder.zig:182-190`), XxHash32 checksum (`utils.zig:11-13` → `std.hash.XxHash32`). `decoder.zig` decodes exactly opcodes 0x00–0x05 with the same mask/flags semantics (`decoder.zig:47-205`); unknown opcodes → `error.InvalidOpcode`.

**Cross-language parity harness is genuine** (`tests.zig`): three tests — `create delta` asserts the Zig-computed delta is **byte-identical** to the C#-generated `.delta.bin` (`tests.zig:172-173`), `apply` asserts round-trip from the C# delta, `round trip` is Zig-create→Zig-apply, plus an `allocation free all` leak check. All read the C#-produced `testdata/manifest.json` (43 vectors, generated 2026-03-16, `src/zig/testdata/manifest.json`).

**`build.zig`** wires a `deltazor` static library (v1.0.0) + a `test` step that auto-generates testdata by shelling out to `dotnet build/run` on `DeltaZor.TestGen` (Windows `cmd /c` script, `build.zig:43-49`). No `build.zig.zon` — acceptable since the code uses only `std` (no external deps).

**OPEN QUESTION (unverifiable here):** `zig` is **not installed on this machine** (`zig: command not found`), so I could **not** run `zig build test` to confirm the parity tests currently pass. A stale `src/zig/zig-out/` exists, indicating it has built before. The XxHash32 commit `f4afe0d` did touch `src/zig/src/utils.zig`, so the checksum upgrade reached Zig. **Whether the Zig parity suite is green today is an explicit open question** — it needs a machine with Zig to confirm.

---

## Parity assessment

**On shipped features (opcodes 0x00–0x05 + checksum + header), parity is asserted byte-for-byte and structurally present on both sides.** The shared test-vector corpus is real (C# `TestGen` produces it; Zig `tests.zig` consumes and byte-compares it). This is the strongest signal that the Plan's "identical algorithms, header format, test vectors" claim is *true for the implemented subset*.

**Parity gaps / nuances found:**
1. **Default threshold mismatch.** C# `DeltaOptions.CompressionThreshold = 1.5` (`DeltaZor.cs:53`); Zig `Options.compression_threshold = 0.95` (`utils.zig:70`). Zig's fallback path even **ignores the option** and hardcodes `* 3 / 2` (= 1.5) at `encoder.zig:203`. Same effective behavior on the shipped path, but the divergent default + dead option field is a real parity/cleanliness defect.
2. **Advanced modes are equally unimplemented on both sides** — so parity is technically preserved (both at zero), but the Plan/README markings overstate completion (see Drift).
3. **Testdata is not committed** (gitignored), so parity verification requires regenerating vectors via C# first. The `build.zig` step automates this but couples the Zig build to a working .NET SDK.

---

## Proposed epic / task decomposition

Statuses grounded in code, reconciled against `Plan.md` (code wins). IDs are allocated in the companion Cypher script.

| Epic | Status | Tasks (status) | Evidence |
|---|---|---|---|
| **E1 — Core RLE+XOR delta engine (C#)** | done | Header/varint/SIMD/full-replace fallback (done); ZeroRun/NonZeroRun/Extension/Truncation (done) | `DeltaZor.cs`, `Encoder.cs`, `Decoder.cs`, `Utils.cs`; 97 tests green |
| **E2 — MOTIF repeat opcodes 0x04/0x05 (C#)** | done | Mask-based uniform+varying motif detect/emit/decode (done); lazy single-accumulator (done) | `Encoder.cs:20-424`, `Decoder.cs:74-156`; commit `7aa5dda` |
| **E3 — Zig implementation (RLE+XOR + MOTIF)** | done (pending green-test confirmation) | Encoder port (done); decoder port (done); utils/varint/xxhash (done); build.zig (done); **confirm `zig build test` passes (open)** | `src/zig/src/*.zig`; commit `641afa9 Initial Zig`, `08ce9ab better zig tests` |
| **E4 — Cross-language parity + shared test vectors** | active | C# TestGen corpus 43 vectors (done); Zig byte-identical harness (done); reconcile threshold-default mismatch (backlog); decouple-or-document testdata regeneration (backlog); run Zig suite on a Zig-equipped host (backlog/open) | `DeltaZor.TestGen`, `src/zig/src/tests.zig`, `manifest.json` |
| **E5 — Float/Half/Channel pattern detection (0x06–0x08)** | backlog | FloatRun 0x06 (backlog); HalfRun 0x07 (backlog); ChannelRun 0x08 — struct exists, not wired (backlog) | `Utils.cs:50-58,107-116`; tests skipped; `PATTERN_COUNTS_FEATURE.md` |
| **E6 — Arithmetic / Planar advanced modes (0x09–0x0A)** | backlog | Global/Planar/PerRun arithmetic, RunArithmetic opcode, clamp-aware, auto-mode | `Utils.cs:60-66`; `ArithmeticCompressionTests` all `[SKIP]`; `Plan.md:256-330` |
| **E7 — Checksum modernization CRC32→XxHash32** | done (docs lag) | Code switch C#+Zig (done); **reconcile README/Plan still saying CRC32 (backlog doc task)** | commit `f4afe0d`; `docs/.../2026-03-16_xxhash32-checksum-upgrade.md`; `README.md:?`, `Plan.md:56,105-111` |
| **E8 — CI / dual-language packaging** | backlog (nothing exists) | .NET pack + NuGet publish (tag-driven, mirror wyvern); Zig cross-compile + WASM artifact; PR test gating (dotnet + zig); resolve dual-default-branch; add `build.zig.zon` for Zig package consumption | no `.github/` exists; `Plan.md:367-413` deliverables |
| **E9 — Repo hygiene / cleanup** | backlog | Remove misplaced `src/csharp/src/csharp/.../HalfPrecisionTests.cs` duplicate; prune stale net8/net9 `obj/` artifacts; delete or fast-forward stale `origin/main`; reduce 315 build warnings | git-tracked dup file; `obj/Debug/net8.0`,`net9.0` dirs |

**Counts: 9 epics, ~30 tasks** (see Cypher for exact task list/IDs).

---

## Branch model

**Prior recon ("master == origin/main == origin/master, no divergence") is INCORRECT.**

```
master            d88639d   (checked out, == origin/master)
origin/master     d88639d
origin/main       5394cbc   ← "Initial commit", the FIRST commit in history
```

`origin/main` sits at the repository's initial commit and has never tracked development. Active development is on **`master`** (and `origin/master`). Working tree clean; `master...origin/master` in sync (`git status -sb`). Remote: `git@github.com:Astn/DeltaZor.git`.

**Finding:** there is a **dual-default-branch ambiguity** — both `main` and `origin/master` exist, `main` is a stale stub. This matters for future CI (which branch triggers publish?). Recommend: pick `master` as canonical (matches all real history) and either delete `origin/main` or fast-forward it to `master`, then set the GitHub default-branch explicitly. Captured as a task under E8/E9.

---

## CI / packaging gap + recommended pipeline

**There is no `.github/` directory** (Glob `.github/**/*` → none) → no Actions, no CI, no NuGet pack/publish, no Zig artifact publishing. For a dual-language library, the missing pipeline should cover:

- **.NET:** `dotnet pack DeltaZor.csproj` → push to NuGet on tag (mirror the sibling **wyvern** tag-driven `publish.yml` convention; list every packable, gate on `NoBuild`, etc.). Only `DeltaZor` (+ maybe `DeltaZor.Shared`) is packable; `TestGen`/`DeltaZorTests` are not.
- **Zig:** `zig build -Doptimize=ReleaseFast` cross-compile matrix (native `.a`/`.dll` + `wasm32-freestanding` `.wasm`) → upload as release artifacts. Add a `build.zig.zon` so the Zig package is consumable via the package manager.
- **PR gating:** on PR run `dotnet test` (needs SDK) AND `zig build test` (needs Zig in CI image, plus the testdata-generation step which currently shells to `dotnet` — CI must have both toolchains, or pre-generate vectors).
- **Branch policy:** resolve the `main`/`master` ambiguity before wiring triggers.

Captured as **Epic E8** with tasks. **No workflow YAML was written in this dispatch** (audit only, per brief).

---

## Doc / code drift findings

1. **CRC32 → XxHash32 (high signal).** `README.md` lists "CRC32 Checksum" as a core feature and `Plan.md:56,105-111` marks CRC32 "✅ Complete." Reality: code is XxHash32 everywhere; zero CRC32 left in `src/`. Commit `f4afe0d` + `docs/.../2026-03-16_xxhash32-checksum-upgrade.md` document the switch. → README + Plan need reconciliation. (Logged as a `Decision` in the graph.)
2. **Channel detection overstated.** `Plan.md:60,141-221` marks Channel Pattern Detection "✅ Complete (`RLE_ChannelRun`)." Reality: opcode constant + `ChannelPattern` struct exist (`Utils.cs:57,107`) but it is **never emitted or decoded** and `OpCodeCounts` flags it `// (Planned)` (`DeltaZor.cs:123`). It is backlog, not done.
3. **Float/Half "In Progress" overstated.** `Plan.md:260-261` marks Global/Planar Arithmetic "✅ In Progress (Float Detection)." The arithmetic tests are all `[SKIP]`; no float/arith opcode is wired. `SUMMARY.md` (Half precision) describes tests that reference files (`HalfPrecisionTests.cs`) that are **misplaced** (see #5) and skipped.
4. **Threshold default divergence** (C# 1.5 vs Zig 0.95) — see Parity #1.
5. **Misplaced duplicate source file.** `src/csharp/src/csharp/DeltaZorTests/UnitTests/HalfPrecisionTests.cs` is git-tracked but lives in a nested `src/csharp/src/csharp/...` path **outside** the `.sln`'s `DeltaZorTests` project dir — a stray copy-paste artifact, not compiled. Cleanup.
6. **Stale obj artifacts.** `net8.0` / `net9.0` `obj/` dirs persist under several projects though all TFMs are now `net10.0` — harmless but noise; `.gitignore` already excludes `obj/`.

---

## Open questions

- **Does `zig build test` pass today?** Unverifiable here (no Zig toolchain on this machine). The harness and source are present and the checksum upgrade reached Zig, but green status needs a Zig-equipped host. **This is the one material unverified claim.**
- Is `master` the intended canonical/publish branch, or is a future migration to `main` planned? (Affects CI trigger + the `origin/main` cleanup decision.)
- Should `DeltaZor.Shared` be packed/published, or is it test-only infrastructure?

## Recommended next action

1. **Apply the companion graph-onboarding Cypher** (`2026-05-28_deltazor-graph-onboarding.cypher`) once memgraph MCP is reachable — it was **not** applied live this session (tool unavailable). Reconcile IDs against the live max first (see script header).
2. **Confirm Zig green** on a Zig-equipped host (`zig build test` in `src/zig/`) to close the one open verification — this gates marking E3 fully `done`.
3. **Doc reconciliation pass** (E7 + drift #1–3): fix README/Plan CRC32→XxHash32 and downgrade Channel/Float/Arith from "complete/in-progress" to "backlog."
4. **Decide branch canonicality** and clean up `origin/main` before any CI work (E8).

> **Cross-kind (codex) second opinion warranted?** Yes, on two judgment calls: (a) **the true Zig state** — I read the source and it is clearly near-parity, but I could not execute the test suite; a codex pass on a Zig-equipped host would independently confirm green/red. (b) **Epic granularity** (9 epics) is a defensible-but-subjective carve-up; a quick cross-model sanity check before committing the graph structure is cheap insurance. Both are optional, not blocking.

---

## Orchestrator reconciliation + as-applied graph state (2026-05-28)

The intake sub-agent produced this report + the companion Cypher with placeholder IDs because the memgraph MCP tool was unregistered in its session. The orchestrator reconciled and **applied the onboarding live** via direct bolt (`bolt://127.0.0.1:34637`; container up + restarted per `reference-aspire-mcp-host-detach`; MCP harness-registration was the lagging piece). Three corrections were made against live evidence before applying:

1. **Project node already existed.** The indexer had already auto-created a `deltazor` `Project` node (lowercase id, rich PM fields: `branch`, `dirty`, `task_count`, `pm_lane`, …) with **zero** epics/tasks/decisions. The apply therefore `MERGE`s onto the existing node and never clobbers indexer-owned fields — it only adds the epic/task/decision structure. This also confirmed the project-id convention is **lowercase `deltazor`** (siblings: `arc-agi`, `digvolleyball`); the sub-agent's draft used mixed-case `DeltaZor`, which would have created a **duplicate** node. Corrected.
2. **Real IDs allocated against live max.** Live max was numeric `EPIC-0040` / `TASK-0349`. Allocated **Epics `EPIC-0041`..`EPIC-0049`** and **Tasks `TASK-0350`..`TASK-0377`** (placeholders `EPIC-90xx`/`TASK-90xx` discarded). Relationship + property conventions verified against the `graph-context` skill, `infra/memgraph/schema.cypher`, and live sibling node shapes (`HAS_EPIC`/`CONTAINS`/`IN_PROJECT`; epics carry `title/status/project/summary`).
3. **EPIC-0043 (Zig) = `active`, not `done`.** Because `zig build test` green status is unverifiable on this host (no Zig toolchain), the Zig epic is honestly `active` with `TASK-0356` as the explicit open verification gate — rather than marking the epic done on unverified evidence (per the no-papering rule).
4. **Decision date corrected to `2026-03-17`** (the actual `f4afe0d` commit date; the sub-agent had inferred `2026-03-16` from the testdata manifest `generatedAt`). Decision id: `DECISION-2026-03-17-xxhash32-checksum`.

### Verified read-back (post-apply)

| Epic | Status | Tasks |
|---|---|---|
| EPIC-0041 Core RLE+XOR delta engine (C#) | done | 2 |
| EPIC-0042 MOTIF repeat opcodes 0x04/0x05 (C#) | done | 1 |
| EPIC-0043 Zig implementation (RLE+XOR + MOTIF) | active | 4 |
| EPIC-0044 Cross-language parity + shared test vectors | active | 4 |
| EPIC-0045 Float/Half/Channel pattern detection (0x06-0x08) | paused | 3 |
| EPIC-0046 Arithmetic / Planar advanced modes (0x09-0x0A) | paused | 3 |
| EPIC-0047 Checksum modernization CRC32→XxHash32 | done | 2 |
| EPIC-0048 CI / dual-language packaging (NuGet + Zig/WASM) | active | 5 |
| EPIC-0049 Repo hygiene / cleanup | active | 4 |

Totals: **9 epics, 28 tasks (9 done / 19 backlog), 1 Decision** (`DECISION-2026-03-17-xxhash32-checksum`, with `TASK-0367 -[IMPLEMENTS]->` and `TASK-0368 -[RELATES_TO]->`). Confirmed exactly one `deltazor` Project node (no mixed-case duplicate).

The companion `2026-05-28_deltazor-graph-onboarding.cypher` was regenerated to mirror exactly what was applied (real IDs, lowercase project, `active` Zig epic, `2026-03-17` decision) and is idempotent for re-apply.

### Remaining open items (now tracked as graph tasks)

- **`zig build test` green** — `TASK-0356` (gates EPIC-0043 → done). Needs a Zig-equipped host.
- Branch canonicality (`master` vs `main`) — `TASK-0373`/`TASK-0374`.
- A cross-kind (codex) second opinion on Zig state + epic granularity remains available but is **not blocking** — the structure is schema-validated and the one hard-unverified claim is explicitly gated by `TASK-0356`.
