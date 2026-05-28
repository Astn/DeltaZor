// ============================================================================
// DeltaZor - graph-rag onboarding (idempotent) -- AS APPLIED 2026-05-28
// Companion report: docs/develop/2026-05-28_deltazor-intake-audit.md
// ----------------------------------------------------------------------------
// Applied via direct bolt (memgraph MCP tool was unregistered in-session; the
// container + bolt were up, restart per reference-aspire-mcp-host-detach done).
// IDs allocated against live max at apply time: max numeric EPIC-0040, TASK-0349
//   -> Epics EPIC-0041..EPIC-0049 ; Tasks TASK-0350..TASK-0377.
// The 'deltazor' Project node ALREADY EXISTED (indexer-created, lowercase id);
// this script MERGEs onto it and never clobbers indexer-owned fields.
// Conventions verified against graph-context skill + infra/memgraph/schema.cypher
// + live sibling node shapes (Project id==name lowercase; HAS_EPIC/CONTAINS/IN_PROJECT).
// ============================================================================

// 1. PROJECT (merge onto existing indexer node; preserve its fields)
MERGE (p:Project {id:'deltazor'})
  ON CREATE SET p.name='deltazor', p.path='C:/Users/austi/src/DeltaZor', p.created_at=datetime()
  SET p.updated_at=datetime(), p.updated_by_agent=true;

// 2. EPICS (HAS_EPIC: Project -> Epic)
UNWIND [
  {id:'EPIC-0041', title:'Core RLE+XOR delta engine (C#)', status:'done', summary:'Foundation opcodes 0x00-0x03 + unified header + varint + SIMD XOR + full-replace fallback. Fully implemented in src/csharp/DeltaZor; 97 unit tests green.'},
  {id:'EPIC-0042', title:'MOTIF repeat opcodes 0x04/0x05 (C#)', status:'done', summary:'Mask-based, lazy single-accumulator motif detection for UnitSizes 2-8; commit 7aa5dda. Encoder.cs/Decoder.cs.'},
  {id:'EPIC-0043', title:'Zig implementation (RLE+XOR + MOTIF)', status:'active', summary:'Full Zig port at src/zig/src/*.zig (encoder/decoder/utils + build.zig). Source + prior zig-out build present; GREEN test status UNVERIFIED (zig not installed on audit/orchestrator host) so epic stays active until TASK-0356 confirms zig build test passes.'},
  {id:'EPIC-0044', title:'Cross-language parity + shared test vectors', status:'active', summary:'C# TestGen emits 43 shared vectors; Zig tests.zig byte-compares against them. Open: threshold default mismatch (C# 1.5 vs Zig 0.95) + testdata regen coupling.'},
  {id:'EPIC-0045', title:'Float/Half/Channel pattern detection (0x06-0x08)', status:'paused', summary:'Opcode constants reserved (Utils.cs) but never emitted/decoded; all related tests skipped. Plan.md overstates Channel as Complete.'},
  {id:'EPIC-0046', title:'Arithmetic / Planar advanced modes (0x09-0x0A)', status:'paused', summary:'Constants only; ArithmeticCompressionTests all skipped; Plan.md marks In Progress (overstated).'},
  {id:'EPIC-0047', title:'Checksum modernization CRC32 to XxHash32', status:'done', summary:'commit f4afe0d (2026-03-17): both C# and Zig moved to XxHash32; zero CRC32 refs remain in src/. Docs still lag (TASK-0368).'},
  {id:'EPIC-0048', title:'CI / dual-language packaging (NuGet + Zig/WASM)', status:'active', summary:'No .github/ exists at all. Needs .NET pack+publish, Zig cross-compile+WASM, PR test-gating, dual-default-branch resolution.'},
  {id:'EPIC-0049', title:'Repo hygiene / cleanup', status:'active', summary:'Stale origin/main, misplaced duplicate test file, stale net8/net9 obj artifacts, 315 build warnings.'}
] AS e
MERGE (ep:Epic {id:e.id})
  ON CREATE SET ep.created_at=datetime()
  SET ep.title=e.title, ep.status=e.status, ep.project='deltazor', ep.summary=e.summary,
      ep.updated_at=datetime(), ep.updated_by_user='austin', ep.updated_by_agent=true
WITH ep MATCH (p:Project {id:'deltazor'}) MERGE (p)-[:HAS_EPIC]->(ep);

// 3. TASKS (CONTAINS: Epic -> Task ; IN_PROJECT: Task -> Project)
UNWIND [
  {id:'TASK-0350', epic:'EPIC-0041', status:'done', title:'Unified header + 7-bit varint + SIMD XOR + full-replace fallback (C#)', notes:'DeltaZor.cs header [len:4][type:1][data][hash:4] self-described by bit7; Utils.cs Write7BitEncodedInt + WriteXORDelta/ApplyXORDelta Vector128.'},
  {id:'TASK-0351', epic:'EPIC-0041', status:'done', title:'Core opcodes 0x00-0x03 ZeroRun/NonZeroRun/Extension/Truncation (C#)', notes:'Encoder.cs CreateRLEDelta ~426-487 emit; Decoder.cs ApplyRLEDelta ~41-72 decode; 97 unit tests green.'},
  {id:'TASK-0352', epic:'EPIC-0042', status:'done', title:'MOTIF uniform 0x04 + varying 0x05, mask-based, lazy single-accumulator (C#)', notes:'Encoder.cs MotifAccumulator/FindMotifCandidate/EmitMotif ~20-424; Decoder.cs ~74-156; commit 7aa5dda.'},
  {id:'TASK-0353', epic:'EPIC-0043', status:'done', title:'Zig encoder port (createDelta + RLE + motif detect)', notes:'src/zig/src/encoder.zig ~379 lines; same probe table {4,8,2,3,5,6,7}, same thresholds; commit 641afa9.'},
  {id:'TASK-0354', epic:'EPIC-0043', status:'done', title:'Zig decoder port (all opcodes 0x00-0x05)', notes:'src/zig/src/decoder.zig ~47-205; unknown opcode to error.InvalidOpcode; identical mask/flags semantics.'},
  {id:'TASK-0355', epic:'EPIC-0043', status:'done', title:'Zig utils (xxhash32, varint, opcode constants, Options/Stats) + build.zig', notes:'src/zig/src/utils.zig std.hash.XxHash32; build.zig library v1.0.0 + test step (commit 08ce9ab).'},
  {id:'TASK-0356', epic:'EPIC-0043', status:'backlog', title:'Confirm zig build test passes on a Zig-equipped host', notes:'OPEN VERIFICATION GATE: zig not installed on audit/orchestrator machine; src/zig/zig-out/lib exists (built 2026-03-16). Source + harness present; green status unverified. Gates EPIC-0043 to done.'},
  {id:'TASK-0357', epic:'EPIC-0044', status:'done', title:'C# TestGen shared test-vector corpus (43 cases)', notes:'DeltaZor.TestGen Program.cs + TestCases/*; testdata/manifest.json totalTests:43 generatedAt 2026-03-16.'},
  {id:'TASK-0358', epic:'EPIC-0044', status:'done', title:'Zig byte-identical parity harness consuming C# vectors', notes:'src/zig/src/tests.zig: create/apply/round-trip assert byte-identical delta vs C# .delta.bin (tests.zig ~172-173).'},
  {id:'TASK-0359', epic:'EPIC-0044', status:'backlog', title:'Reconcile threshold default mismatch C# 1.5 vs Zig 0.95', notes:'DeltaZor.cs ~:53 CompressionThreshold=1.5; utils.zig ~:70 compression_threshold=0.95; encoder.zig ~:203 hardcodes *3/2 ignoring the option.'},
  {id:'TASK-0360', epic:'EPIC-0044', status:'backlog', title:'Decouple or document testdata regeneration (build.zig shells to dotnet)', notes:'testdata/ is gitignored; build.zig ~43-49 shells dotnet build/run TestGen. Couples Zig build to .NET SDK.'},
  {id:'TASK-0361', epic:'EPIC-0045', status:'backlog', title:'Implement FloatRun 0x06 (C# + Zig)', notes:'Constant only: Utils.cs ~:50 RLE_FloatRun; never emitted/decoded; no float opcode wired.'},
  {id:'TASK-0362', epic:'EPIC-0045', status:'backlog', title:'Implement HalfRun 0x07 (C# + Zig)', notes:'Constant only: Utils.cs ~:53 RLE_HalfRun; SUMMARY.md HalfPrecision tests reference misplaced file (see TASK-0375).'},
  {id:'TASK-0363', epic:'EPIC-0045', status:'backlog', title:'Wire ChannelRun 0x08 into encoder/decoder (C# + Zig)', notes:'ChannelPattern struct exists Utils.cs ~107-116 but AnalyzeChannelPattern absent; opcode never emitted/decoded; Plan.md overstates as Complete.'},
  {id:'TASK-0364', epic:'EPIC-0046', status:'backlog', title:'Global + Planar arithmetic detection (0x09/0x0A)', notes:'Constants only Utils.cs ~60-66; ArithmeticCompressionTests Global/Planar skipped; Plan.md ~260-261 marks In Progress (overstated).'},
  {id:'TASK-0365', epic:'EPIC-0046', status:'backlog', title:'Per-run arithmetic + RunArithmetic opcode + clamp-aware', notes:'ArithmeticCompressionTests PerRun/RunArithmetic/Clamp skipped; Plan.md ~289-315.'},
  {id:'TASK-0366', epic:'EPIC-0046', status:'backlog', title:'Auto-mode best-of selection across modes', notes:'ArithmeticCompressionTests AutoModeSelection skipped; Plan.md ~317-330.'},
  {id:'TASK-0367', epic:'EPIC-0047', status:'done', title:'Switch checksum CRC32 to XxHash32 (C# + Zig)', notes:'commit f4afe0d (2026-03-17); DeltaZor.cs ~218,327 XxHash32Wrapper; utils.zig ~11-13 std.hash.XxHash32; zero CRC32 refs remain in src/.'},
  {id:'TASK-0368', epic:'EPIC-0047', status:'backlog', title:'Doc reconciliation: README/Plan still say CRC32', notes:'README.md core-features lists CRC32; Plan.md ~56,105-111 marks CRC32 Complete. Update to XxHash32 (also downgrade Channel/Float/Arith claims).'},
  {id:'TASK-0369', epic:'EPIC-0048', status:'backlog', title:'.NET pack + tag-driven NuGet publish (mirror wyvern publish.yml)', notes:'No .github/ exists. Only DeltaZor (+maybe Shared) packable; TestGen/Tests not. Plan.md ~404-413 deliverables.'},
  {id:'TASK-0370', epic:'EPIC-0048', status:'backlog', title:'Zig cross-compile matrix + WASM artifact upload', notes:'native .a/.dll + wasm32-freestanding .wasm; build.zig has library target; Plan.md ~367-413.'},
  {id:'TASK-0371', epic:'EPIC-0048', status:'backlog', title:'Add build.zig.zon for Zig package consumption', notes:'No build.zig.zon present; needed for Zig package manager distribution.'},
  {id:'TASK-0372', epic:'EPIC-0048', status:'backlog', title:'PR test-gating workflow (dotnet test + zig build test)', notes:'CI image needs both .NET SDK and Zig; testdata gen step shells to dotnet so pre-generate or install both.'},
  {id:'TASK-0373', epic:'EPIC-0048', status:'backlog', title:'Resolve dual-default-branch ambiguity before wiring CI triggers', notes:'origin/main stuck at initial commit 5394cbc (2025-10-25); origin/master==master==d88639d. Pick canonical, set GH default, clean origin/main.'},
  {id:'TASK-0374', epic:'EPIC-0049', status:'backlog', title:'Delete or fast-forward stale origin/main (initial commit only)', notes:'git rev-parse origin/main = 5394cbc (first commit); never tracked dev. Companion of TASK-0373.'},
  {id:'TASK-0375', epic:'EPIC-0049', status:'backlog', title:'Remove misplaced duplicate src/csharp/src/csharp/.../HalfPrecisionTests.cs', notes:'git-tracked but in nested src/csharp/src/csharp path outside the .sln DeltaZorTests project; stray copy-paste, not compiled.'},
  {id:'TASK-0376', epic:'EPIC-0049', status:'backlog', title:'Prune stale net8.0/net9.0 obj artifacts', notes:'obj/Debug/net8.0 + net9.0 dirs persist though all TFMs are net10.0; harmless noise, obj/ already gitignored.'},
  {id:'TASK-0377', epic:'EPIC-0049', status:'backlog', title:'Reduce 315 build warnings (CS1591 in TestGen, nullability/xUnit in tests)', notes:'dotnet build DeltaZor.sln 2026-05-28: 0 errors, 315 warnings; not core defects but noise.'}
] AS t
MERGE (task:Task {id:t.id})
  ON CREATE SET task.created_at=datetime()
  SET task.title=t.title, task.status=t.status, task.project='deltazor', task.epic=t.epic,
      task.notes=t.notes, task.priority=coalesce(task.priority,2),
      task.updated_at=datetime(), task.updated_by_user='austin', task.updated_by_agent=true
WITH task, t
FOREACH (_ IN CASE WHEN t.status='done' AND task.completed_at IS NULL THEN [1] ELSE [] END | SET task.completed_at=datetime())
WITH task, t MATCH (ep:Epic {id:t.epic}) MERGE (ep)-[:CONTAINS]->(task)
WITH task MATCH (p:Project {id:'deltazor'}) MERGE (task)-[:IN_PROJECT]->(p);

// 4. DECISION - CRC32 -> XxHash32 (commit f4afe0d, 2026-03-17)
MERGE (d:Decision {id:'DECISION-2026-03-17-xxhash32-checksum'})
  ON CREATE SET d.created_at=datetime()
  SET d.title='Upgrade delta checksum from CRC32 to XxHash32', d.status='accepted',
      d.date=date('2026-03-17'), d.summary='Replaced CRC32 with XxHash32 in both C# (System.IO.Hashing.XxHash32) and Zig (std.hash.XxHash32). Same 4-byte wire field, faster, zero new deps, no wire/header/API change. Implemented commit f4afe0d (2026-03-17). README/Plan docs still lag (see TASK-0368).', d.project='deltazor',
      d.updated_at=datetime(), d.updated_by_user='austin', d.updated_by_agent=true
WITH d MATCH (p:Project {id:'deltazor'}) MERGE (d)-[:IN_PROJECT]->(p);
MATCH (t:Task {id:'TASK-0367'}),(d:Decision {id:'DECISION-2026-03-17-xxhash32-checksum'}) MERGE (t)-[:IMPLEMENTS]->(d);
MATCH (t:Task {id:'TASK-0368'}),(d:Decision {id:'DECISION-2026-03-17-xxhash32-checksum'}) MERGE (t)-[:RELATES_TO]->(d);

// 5. READ-BACK (note: ORDER BY must use the alias, not e.id, after aggregation in Memgraph)
// MATCH (p:Project {id:'deltazor'})-[:HAS_EPIC]->(e:Epic)
// OPTIONAL MATCH (e)-[:CONTAINS]->(t:Task)
// WITH e, count(t) AS tasks RETURN e.id AS epic, e.status AS status, tasks ORDER BY epic;
