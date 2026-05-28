# TASK-0368 — Doc/Code Reconciliation — Execution Log

**Date:** 2026-05-28
**Agent:** DEV (claude-fallback; codex CLI was available but the work was
self-contained, already fully grounded by greps, and codex's daily budget is
constrained — see "impl-kind" below)
**Project:** `deltazor` · EPIC-0047 · TASK-0368
**Working dir:** `C:/Users/austi/src/DeltaZor` · branch `master`
**Scope:** docs-only. No `.cs` / `.zig` / build / test files touched.
**Grounding:** `docs/develop/2026-05-28_deltazor-intake-audit.md` (Doc/code drift
findings) + direct code greps run this session.

---

## What changed (per file)

### `Plan.md` (only source file edited)

1. **CRC32 → XxHash32** (drift #1):
   - Roadmap row 5: `**CRC32 Checksum** … ✅ Complete (`Crc32.Compute`) | `crc32.zig`
     (lookup table)` → `**XxHash32 Checksum** … ✅ Complete
     (`XxHash32Wrapper.Compute` → `System.IO.Hashing.XxHash32`) | `std.hash.XxHash32`
     (`utils.zig`)`.
   - Section 5 header `## 5. CRC32 Checksum` → `## 5. XxHash32 Checksum`; body table
     updated (`System.IO.Hashing.XxHash32.HashToUInt32` / `std.hash.XxHash32.hash`).
   - Roadmap row 8 header layout `[len:4][type:1][data][crc:4]` → `…[checksum:4]`.

2. **Channel detection: Complete → Planned** (drift #2):
   - Roadmap row 9: `✅ Complete (`RLE_ChannelRun`)` → `📋 Planned` with the exact
     code state (opcode constant + `ChannelPattern` struct reserved in `Utils.cs`;
     `AnalyzeChannelPattern` absent; opcode never emitted/decoded;
     `OpCodeCounts.ChannelRunCount` marked `// (Planned)`).
   - "Completed Work" list: removed the false `✅ Channel Pattern Detection:
     Implemented…` bullet; the surviving Pattern Counts bullet now notes the
     `ChannelRunCount` slot is reserved and always reads 0.
   - Section 9 header `## 9. Channel Pattern Detection` → `… (Planned)` with a
     **Status** banner stating it is not implemented and the code blocks are
     illustrative of the planned shape (kept as roadmap design intent).

3. **Float/Half + Arithmetic: In Progress → Planned** (drift #3):
   - Advanced-features rows 11/12: `✅ In Progress (Float Detection)` →
     `📋 Planned` citing `RLE_Arithmetic = 0x09` / `RLE_Planar = 0x0A` constants
     reserved, not wired, and `ArithmeticCompressionTests` all `[Skip]`.
   - "Completed Work" `✅ Low-Risk Float Pattern Detection` →
     `📋 Float Pattern Detection (Planned)` — no float detector exists in the core
     library; `FloatPatternCount` is reserved (`DeltaZor.cs`, `// 0x06 (Planned)`)
     and always reads 0.
   - Milestones: v0.3 (Arithmetic) and v0.5 (Channel Runs) no longer claim
     `✅ Complete`; split into the parts that are actually done (motif refinement, Pattern
     Counts) vs. planned (Arithmetic, Channel Runs). v0.4 `+1 week` → `📋 Planned`.

     (Note: motif refinement + Pattern Counts are the genuinely-done parts.)

### Files deliberately NOT edited

- **`README.md`** — already accurate: no CRC32 anywhere; Channel/Float/Half/
  Arithmetic correctly listed under "Pending Features (TBD)". The brief's premise
  that README lists "CRC32 Checksum" did not hold against the current file (it had
  already been cleaned). Verified by `grep -i crc32 README.md` → 0.
- **`SUMMARY.md`** — out of scope per brief (its HalfPrecision content references a
  misplaced test file = TASK-0375). Makes no false *checksum* claim (no CRC32).
- **`MOTIF_REPEAT_OPCODES.md`** and `docs/architecture/**`, `docs/develop/**` —
  contain CRC32 only as historical design records; brief scopes to README/Plan.

---

## Verification (commands run this session)

| Check | Command | Result |
|---|---|---|
| CRC32 gone from Plan | `grep -ri crc32 Plan.md` | **0 matches** |
| CRC32 gone from README | `grep -ri crc32 README.md` | **0 matches** |
| Code has no CRC32 | `grep -ri crc32 src/` | **0 matches** |
| XxHash32 is the impl | `grep -n XxHash32 src/` | `DeltaZor.cs:218,327` (`XxHash32Wrapper.Compute`); `Utils.cs:342-346` (`XxHash32Wrapper` → `System.IO.Hashing.XxHash32.HashToUInt32`); `zig/src/utils.zig:12` (`std.hash.XxHash32.hash`) |
| Channel opcode not wired | `grep RLE_ChannelRun\|AnalyzeChannelPattern Encoder.cs Decoder.cs` | **0 matches** (constant+struct only in `Utils.cs:57,107-116`; `DeltaZor.cs:123` `// (Planned)`) |
| Float/Half/Arith not wired | `grep RLE_FloatRun\|RLE_HalfRun\|RLE_Arithmetic Encoder.cs Decoder.cs` | **0 matches** (constants `// Pending` only, `Utils.cs:50-66`); `ArithmeticCompressionTests` all `[Skip]` |
| Checksum reality SHA | `git log --oneline -1 f4afe0d` | `f4afe0d feat: Upgrade checksum from CRC32 to XxHash32` |

Re-read of edited Plan.md sections confirms **no remaining false
"Complete"/"In Progress" labels** on Channel / Float / Arithmetic.

---

## Handoff packet

- **Status:** review (cross-kind claude audit pending).
- **Commit SHA:** `42b3346` (single commit on `master`, not pushed; this exec-log
  SHA-backfill line was added in a tiny follow-up commit on top).
- **Files changed:** `Plan.md` (+ this exec log).
- **Evidence paths:** this log; `docs/develop/2026-05-28_deltazor-intake-audit.md`
  (drift §); commit `f4afe0d` (checksum reality);
  `docs/architecture/2026-03-16_xxhash32-checksum-upgrade.md`.
- **Verification:** grep table above (CRC32 = 0 in docs + code; XxHash32 present).
- **Graph updates:** `TASK-0368` → `status='review'`, note appended with commit SHA.
- **impl-kind:** `claude-fallback` (codex CLI present @ v0.125.0, but task was fully
  grounded and self-contained; ran directly to conserve codex budget).
- **Open risks:** none for this docs scope. README was already clean (brief premise
  was stale for that file — noted above). Other historical docs
  (`MOTIF_REPEAT_OPCODES.md`, `docs/architecture/*`) still carry CRC32 as design
  history — intentionally untouched (out of scope; not false-claim "Complete"
  status, just historical narrative).
- **Next:** cross-kind claude audit (re-run greps independently; confirm no
  `.cs`/`.zig`/build/test files were modified; confirm wording is accurate, not
  aspirational).

---

## Cross-kind audit (codex, direct)

**Date:** 2026-05-28  
**Auditor:** codex direct cross-kind audit  
**Stop-condition check:** passed.

```text
> git log --oneline -2
a3c1c2b docs(deltazor): TASK-0368 — backfill exec-log commit SHA reference
42b3346 docs(deltazor): TASK-0368 — reconcile README/Plan with code (XxHash32; Channel/Float/Arith are planned)
```

### A. Docs-only

**Result:** PASS. Commit `42b3346` touched only `Plan.md` and this execution log; no `.cs`, `.zig`, build, or test files changed.

```text
> git show --stat --oneline --decorate --name-only 42b3346
42b3346 docs(deltazor): TASK-0368 — reconcile README/Plan with code (XxHash32; Channel/Float/Arith are planned)
Plan.md
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md

> git diff --stat 42b3346^ 42b3346
 Plan.md                                            |  29 +++---
 ...05-28_task-0368-doc-reconciliation-execution.md | 103 +++++++++++++++++++++
 2 files changed, 118 insertions(+), 14 deletions(-)

> git diff --name-status 42b3346^ 42b3346
M	Plan.md
A	docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md
```

### B. CRC32 -> XxHash32 accuracy

**Result:** PASS for `Plan.md`/`README.md` and source reality. `Plan.md` and `README.md` have zero `crc32` matches; the C# and Zig implementation paths use XxHash32; the `.cs`/`.zig` source grep for `crc32` is empty.

```text
> rg -n -i "crc32" Plan.md README.md
<no output>

> rg -n "XxHash32" src/csharp/DeltaZor/DeltaZor.cs src/zig/src/utils.zig
src/csharp/DeltaZor/DeltaZor.cs:218:        uint checksum = options.EnableChecksum ? XxHash32Wrapper.Compute(newData) : 0;
src/csharp/DeltaZor/DeltaZor.cs:327:            uint actualChecksum = XxHash32Wrapper.Compute(output.Slice(0, outputLength));
src/zig/src/utils.zig:12:    return std.hash.XxHash32.hash(0, data);

> rg -n -i "crc32" src -g "*.cs" -g "*.zig"
<no output>
```

### C. Channel / Float / Arith downgrade accuracy

**Result:** PASS for the main downgrade claim, with one related doc finding covered under E. Code supports "Planned" for Channel, Float/Half, Arithmetic, and Planar: constants/structs/counters exist, but encoder/decoder references are absent and arithmetic tests are skipped.

```text
> rg -n "RLE_ChannelRun|ChannelPattern|AnalyzeChannelPattern|Channel" src/csharp src/zig
src/csharp\DeltaZor\DeltaZor.cs:123:        public int ChannelRunCount { get; set; } // 0x08 (Planned)
src/csharp\DeltaZor\Utils.cs:57:        RLE_ChannelRun =
src/csharp\DeltaZor\Utils.cs:58:            0x08; // Pending: Channel-optimized runs; [opcode:1][count:7bit][channels:1][mask:1][changed_data:variable].
src/csharp\DeltaZor\Utils.cs:107:    internal readonly struct ChannelPattern
src/csharp\DeltaZor\Utils.cs:109:        public int Channels { get; init; }
src/csharp\DeltaZor\Utils.cs:110:        public byte ChannelMask { get; init; }
src/csharp\DeltaZor\Utils.cs:111:        public int ChangedChannels { get; init; }
src/csharp\DeltaZor\Utils.cs:115:        public static ChannelPattern None => new() { IsBeneficial = false };

> rg -n "AnalyzeChannelPattern|RLE_ChannelRun|RLE_FloatRun|RLE_HalfRun|RLE_Arithmetic|RLE_Planar" src/csharp/DeltaZor/Encoder.cs src/csharp/DeltaZor/Decoder.cs src/zig/src/encoder.zig src/zig/src/decoder.zig
<no output>

> rg -n "RLE_FloatRun|RLE_HalfRun|RLE_ChannelRun|RLE_Arithmetic|RLE_Planar|RLE_UniformMotifRepeat|RLE_VaryingMotifRepeat" src/csharp/DeltaZor/Utils.cs src/zig/src/utils.zig
src/csharp/DeltaZor/Utils.cs:44:        RLE_UniformMotifRepeat = 0x04; // Partial: Chunk-less mask-based uniform repeats; high priority for full impl.
src/csharp/DeltaZor/Utils.cs:47:        RLE_VaryingMotifRepeat = 0x05; // Partial: Chunk-less mask-based varying repeats; high priority for full impl.
src/csharp/DeltaZor/Utils.cs:50:        RLE_FloatRun = 0x06; // Pending: Specialized for float32 runs; [opcode:1][count:7bit][float_xor_data:count*4].
src/csharp/DeltaZor/Utils.cs:53:        RLE_HalfRun =
src/csharp/DeltaZor/Utils.cs:57:        RLE_ChannelRun =
src/csharp/DeltaZor/Utils.cs:61:        RLE_Arithmetic =
src/csharp/DeltaZor/Utils.cs:65:        RLE_Planar =

> rg -n "FloatPatternCount|HalfPatternCount|ChannelRunCount|UniformMotifCount|VaryingMotifCount" src/csharp/DeltaZor/DeltaZor.cs
113:        public int UniformMotifCount { get; set; } // 0x04 (Implemented)
114:        public int VaryingMotifCount { get; set; } // 0x05 (Implemented)
118:                                        ChannelRunCount + UniformMotifCount + VaryingMotifCount;
121:        public int FloatPatternCount { get; set; } // 0x06 (Planned)
122:        public int HalfPatternCount { get; set; } // 0x07 (Planned)
123:        public int ChannelRunCount { get; set; } // 0x08 (Planned)

> rg -n "Fact\(|Theory\(|Skip" src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs src/csharp/src/csharp/DeltaZorTests/UnitTests/HalfPrecisionTests.cs
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:10:        [Fact(Skip = "Arithmetic compression not yet implemented")]
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:38:        [Fact(Skip = "Not yet implemented")]
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:46:        [Fact(Skip = "Not yet implemented")]
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:54:        [Fact(Skip = "Arithmetic compression not yet implemented")]
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:86:        [Fact(Skip = "Not yet implemented")]
src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs:94:        [Fact(Skip = "Not yet implemented")]
```

### D. README correctly left unedited

**Result:** PASS. `README.md` has no CRC32 claim and correctly places ChannelRun, Float/Half, and Arithmetic/Planar under Pending Features.

```text
> rg -n "CRC|XxHash|checksum|Pending Features|Arithmetic|Channel|Float|Half|planned|Planned|Implemented|Status" README.md
5:## Core Features (Implemented)
9:- MOTIF Repeats (0x04 Uniform, 0x05 Varying): Implemented with chunk-less mask-based contiguous packing for repeating patterns, featuring lazy, single-accumulator detection for variable UnitSizes 2-8 in a single-pass, allocation-free manner.
11:## Pending Features
12:- ChannelRun (TBD) for structured data (integratable with MOTIF).
13:- Float/Half Runs (TBD).
14:- Arithmetic/Planar (TBD).
```

### E. Greenfield / no-papering

**Result:** FAIL. The main roadmap downgrades are accurate, and I found no migration narrative in `Plan.md` or `README.md`; however, `Plan.md` still contains status/benchmark rows with `✅` claims for advanced arithmetic/channel-flavored outcomes that are not supported by the code evidence above. It also has a stale PatternCounts example assigning future Float/Half counters to `0x04`/`0x05`, while real code uses those opcodes for implemented motif repeats and reserves Float/Half as `0x06`/`0x07`.

```text
> rg -n -C 8 "Sparse_1KB|Uniform_Int_1M|Color_Fill|1080p|RGBA_AlphaOnly|v0\.3|v0\.4|v0\.5" Plan.md
383:| `Sparse_1KB` | 1% changed | ✅ <50 B | <50 B |
384:| `Uniform_Int_1M` | +5 | ✅ 8 B | 8 B |
385:| `Color_Fill_200x200` | Fill tool | ✅ ~30 B | ~30 B |
386:| `1080p_Tint` | R+10 | ✅ 20 B | 20 B |
387:| `RGBA_AlphaOnly` | Alpha channel edit | ✅ ~25% of original | ~25% of original |
397:| **v0.3** | Motif refinement with lazy single-accumulator detection (Arithmetic Global + Planar deferred) | 🔄 Motif refinement ✅ done; Arithmetic 📋 planned |
398:| **v0.4** | RunArithmetic + Clamp | 📋 Planned |
399:| **v0.5** | Pattern Counts ✅ done; Channel Runs 📋 planned (opcode reserved, not wired) | 🔄 Partial |

> rg -n -C 6 "Pattern Count|ChannelRunCount|FloatPatternCount|HalfPatternCount|Future|0x04|0x05|0x08" Plan.md
237-public readonly struct PatternCounts
238-{
239-    public int ZeroRunCount { get; init; }        // 0x00
240-    public int NonZeroRunCount { get; init; }     // 0x01
241-    public int ExtensionCount { get; init; }      // 0x02
242-    public int TruncationCount { get; init; }     // 0x03
243:    public int ChannelRunCount { get; init; }     // 0x08
244:    public int FloatPatternCount { get; init; }   // Future: 0x04
245:    public int HalfPatternCount { get; init; }    // Future: 0x05
246-}

> rg -n -i "migration|migrat|legacy|previous|formerly|old checksum|upgrade|changed from|CRC32" Plan.md README.md docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:19:1. **CRC32 → XxHash32** (drift #1):
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:20:   - Roadmap row 5: `**CRC32 Checksum** … ✅ Complete (`Crc32.Compute`) | `crc32.zig`
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:24:   - Section 5 header `## 5. CRC32 Checksum` → `## 5. XxHash32 Checksum`; body table
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:56:- **`README.md`** — already accurate: no CRC32 anywhere; Channel/Float/Half/
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:58:  that README lists "CRC32 Checksum" did not hold against the current file (it had
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:59:  already been cleaned). Verified by `grep -i crc32 README.md` → 0.
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:61:  misplaced test file = TASK-0375). Makes no false *checksum* claim (no CRC32).
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:63:  contain CRC32 only as historical design records; brief scopes to README/Plan.
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:71:| CRC32 gone from Plan | `grep -ri crc32 Plan.md` | **0 matches** |
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:72:| CRC32 gone from README | `grep -ri crc32 README.md` | **0 matches** |
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:73:| Code has no CRC32 | `grep -ri crc32 src/` | **0 matches** |
docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md:77:| Checksum reality SHA | `git log --oneline -1 f4afe0d` | `f4afe0d feat: Upgrade checksum from CRC32 to XxHash32` |
```

### F. Exec-log honesty

**Result:** PARTIAL / FAIL. The log accurately records commit scope, implementation kind `claude-fallback`, README being left alone, and the grep evidence for CRC32/XxHash32 and unimplemented advanced modes. It is not fully honest/complete because it states: "Re-read of edited Plan.md sections confirms no remaining false `Complete`/`In Progress` labels on Channel / Float / Arithmetic." That misses the benchmark table and stale PatternCounts example called out in E.

```text
> rg -n "impl-kind|claude|fallback|grep|CRC32|XxHash32|Plan.md|README|42b3346|a3c1c2b" docs/develop/2026-05-28_task-0368-doc-reconciliation-execution.md
4:**Agent:** DEV (claude-fallback; codex CLI was available but the work was
17:### `Plan.md` (only source file edited)
19:1. **CRC32 → XxHash32** (drift #1):
56:- **`README.md`** — already accurate: no CRC32 anywhere; Channel/Float/Half/
71:| CRC32 gone from Plan | `grep -ri crc32 Plan.md` | **0 matches** |
72:| CRC32 gone from README | `grep -ri crc32 README.md` | **0 matches** |
73:| Code has no CRC32 | `grep -ri crc32 src/` | **0 matches** |
74:| XxHash32 is the impl | `grep -n XxHash32 src/` | `DeltaZor.cs:218,327` (`XxHash32Wrapper.Compute`); `Utils.cs:342-346` (`XxHash32Wrapper` → `System.IO.Hashing.XxHash32.HashToUInt32`); `zig/src/utils.zig:12` (`std.hash.XxHash32.hash`) |
86:- **Status:** review (cross-kind claude audit pending).
87:- **Commit SHA:** `42b3346` (single commit on `master`, not pushed; this exec-log
95:- **impl-kind:** `claude-fallback` (codex CLI present @ v0.125.0, but task was fully
```

### Findings

1. **Blocking:** `Plan.md` benchmark/status table still presents arithmetic/channel examples as current `✅` outcomes (`Uniform_Int_1M`, `Color_Fill_200x200`, `1080p_Tint`, `RGBA_AlphaOnly`), while the implementation greps show those specialized modes are planned-only.
2. **Blocking:** `Plan.md` PatternCounts example says `FloatPatternCount` future opcode `0x04` and `HalfPatternCount` future opcode `0x05`, but actual code uses `0x04`/`0x05` for implemented motif repeats and reserves Float/Half as `0x06`/`0x07`.
3. **Exec-log gap:** the implementation log's "no remaining false labels" statement is too broad given the two Plan findings above.

VERDICT: REVIEW HOLD

---

## Orchestrator-direct remediation (post-codex REVIEW HOLD)

The cross-kind **codex (direct)** audit returned **REVIEW HOLD** with two valid blocking findings the claude-fallback impl (and a claude pre-audit) missed — genuine cross-kind value. Both verified by the orchestrator against `Plan.md` + the opcode map before fixing:

1. **Benchmark table false ✅ (lines ~383-387).** `Uniform_Int_1M` (+5 = arithmetic), `1080p_Tint` (R+10 = channel/arithmetic), `RGBA_AlphaOnly` (channel) were marked **✅ achieved** though those modes are planned-only. Fixed: section retitled "Benchmarks (Projected Targets)" with a status-legend caveat (✅ = core-achievable; 📋 = planned mode / projected target, not implemented; all sizes illustrative — no benchmark harness exists, only the 43-case parity suite). The three planned-mode rows changed ✅ → 📋 (planned). `Sparse_1KB` + `Color_Fill_200x200` remain ✅ (RLE+XOR core).
2. **PatternCounts opcode error (lines 244-245).** `FloatPatternCount // Future: 0x04` and `HalfPatternCount // Future: 0x05` were wrong — `0x04`/`0x05` are the *implemented MOTIF* opcodes; Float/Half are `0x06`/`0x07` (consistent with line 35 and the code). Fixed → `0x06` / `0x07`.

Finding 3 (exec-log "no remaining false labels" was too broad) is resolved by this remediation: the residual false labels are now corrected.

Verified post-fix: `grep ✅ Plan.md | grep <planned-mode terms>` excluding planned/deferred/reserved → **0**; opcode comments now `0x06`/`0x07`.

**Why orchestrator-direct (per feedback-orchestrator-direct-remediation):** findings are concrete, mechanical doc-accuracy fixes found by the cross-kind auditor; impl was claude-fallback; re-dispatching a third agent for two doc edits would be over-process. Cross-kind discipline preserved: impl = claude-fallback, audit = **codex (direct, session 019e6f98)**, remediation = claude orchestrator executing the audit's exact findings.

**Cross-kind tooling note:** the first codex-audit dispatch self-stopped because the brief told the *agent* to "run codex," and when the agent already was codex it tried to nest-spawn codex → `Codex cannot access session files ... permission denied`. Running `codex exec` **directly** from the orchestrator (not nested via an agent) worked and produced the verdict. Lesson captured for future codex dispatch.
