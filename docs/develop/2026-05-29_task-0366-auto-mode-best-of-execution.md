# TASK-0366 — Auto-mode best-of selection across modes (C#+Zig byte-parity) — execution log

- Date: 2026-05-29
- Branch: `task-0366-auto-mode-best-of` (from `master` @ 645cf69)
- Epic: EPIC-0046 (CAPSTONE — completes the arithmetic-modes epic)
- impl-kind: claude (opus), DEV lane. Cross-kind codex audit follows.
- Toolchains run for real (no sandbox): `dotnet test --no-restore -m:1` (PASS); Zig verified for
  real under the installed toolchain (0.16.0) via a 0.16-compatible standalone parity harness —
  see "Zig verification + toolchain blocker" below.

## Context — the capstone after the opcode trio + arithmetic modes

EPIC-0045 (opcodes 0x00–0x08) + EPIC-0046 (arithmetic 0x09/0x0A/0x0B) are done. Those opcodes all
live INSIDE the RLE-delta data stream (compression_type 0x00) and are already individually
best-of'd: each opcode/arithmetic probe emits ONLY when strictly smaller than the alternatives over
its span (their strict-improvement gates). The remaining selection is at the **top level**: the
encoder chooses between two candidate top-level modes —

- `compression_type 0x00` — the RLE-delta-with-opcodes stream (`DeltaEncoder.CreateRLEDelta`)
- `compression_type 0x01` — raw FullReplace (`newData` verbatim, data size == `newData.Length`)

Before this task that top-level choice was a **heuristic**: keep RLE unless its data length exceeds
`newData.Length × CompressionThreshold`, where the **default threshold was 1.5**. That means an RLE
delta up to 1.5× the raw size was KEPT even though FullReplace (1.0× + identical header) was
strictly smaller — a genuine selection gap. The skipped `ArithmeticCompressionTests.
AutoModeSelection_ChoosesBestCompressionMode` documented the contract to close.

## VALUE CHECK — best-of beats the heuristic (real gap, confirmed)

I probed the LIVE encoder (`DeltaEncoder.CreateRLEDelta` via `ArrayBufferWriter`) on aperiodic
isolated single-byte changes (gap of 1 or 2 unchanged bytes, random nonzero XOR values, 4000-byte
buffer) — a shape that defeats motif coalescing and the arithmetic probes, maximising RLE overhead:

```
seed=2 raw=4000 rleData=4133 ratio=1.033 emittedType=0 deltaLen=4138 fullReplaceWouldBe=4009 keptRleButRawSmaller=True
seed=4 raw=4000 rleData=4151 ratio=1.038 emittedType=0 deltaLen=4156 fullReplaceWouldBe=4009 keptRleButRawSmaller=True
```

At the OLD default 1.5, the heuristic KEEPS the RLE delta (4138/4156 bytes) because 1.03 < 1.5, when
FullReplace (4009 bytes) is ~130 bytes smaller. **Best-of picks FullReplace** — a real win the
heuristic missed. So the gap is real → implement (not descope).

Note on the existing boundary vectors (Test044/045): their stale comments describe RLE data of
767/769 bytes at TASK-0405 time, but the EPIC-0045/0046 pipeline has since strengthened so much that
both now compress to RLE data 510/502 bytes (well below raw 512). They no longer bracket any
threshold and stay RLE under both 1.0 and 1.5 — so the value gap is NOT embodied in a committed
vector; it lives in the `AutoModeSelection` unit test's 4000-byte input. (Their comments are
pre-existing staleness, untouched here — out of scope.)

## Design — candidates, selection, deterministic tie-break

- **Candidates:** {RLE-delta stream (mode 0x00, size = `CreateRLEDelta` output length),
  FullReplace (mode 0x01, size = `newData.Length`)}. The 5-byte header and the optional checksum are
  identical for both modes, so comparing the two DATA sizes is the exact total-size comparison.
- **Selection:** pick the candidate with the smaller data size.
- **Deterministic tie-break:** lowest mode-id wins → on an exact size tie, RLE (0x00) is kept over
  FullReplace (0x01). Implemented by the strict `>` comparison `rleLen > rawLen × threshold`:
  at threshold 1.0, `rleLen == rawLen` is NOT `>` so RLE is kept (the tie).

### Implementation — best-of == CompressionThreshold default of 1.0

The existing compare in both languages is ALREADY a deterministic best-of with the RLE-wins
tie-break; the ONLY thing making it sub-optimal was the lenient default threshold 1.5. So genuine
best-of is achieved by setting the **default `CompressionThreshold` to 1.0** in BOTH languages —
zero divergence risk in the selection logic itself (the comparison is byte-identical and was already
mirrored). `CompressionThreshold` is kept as the configurable knob: values > 1.0 deliberately keep a
larger RLE (motif-internals tests use 2.0), values < 1.0 force FullReplace earlier; optimality holds
exactly at 1.0.

Changes:
- `src/csharp/DeltaZor/DeltaZor.cs` — `DeltaOptions.CompressionThreshold` default `1.5 → 1.0`;
  expanded XML doc + the dispatch comment to describe best-of, the candidates, the data-size
  comparison, and the deterministic tie-break + the Zig mirror.
- `src/zig/src/utils.zig` — `Options.compression_threshold` default `1.5 → 1.0` with a mirrored
  comment. `encoder.zig createDeltaWithStats` already uses the identical strict `>` compare against
  `new_data.len × compression_threshold` (f64), so C# and Zig select the SAME mode for any input.

### The byte-parity crux

Auto-mode must pick the SAME top-level mode in C# and Zig for the same input. Both languages use the
identical predicate `rleDataLen > rawLen × threshold` in `f64`/`double` with the same default 1.0 and
the same RLE-wins-on-tie strict `>`. There is no separate selection code path to diverge — the only
shared constant (the threshold default) is now equal in both. Confirmed empirically (below): all 56
cross-toolchain vectors emit the SAME mode byte and are byte-identical C#↔Zig.

## Un-skip AutoModeSelection

`src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs` — removed `Skip` and implemented
three sub-cases asserting genuine optimality:
- **Case A (RLE bloat):** the 4000-byte aperiodic value-gap input — best-of MUST emit FullReplace
  (0x01); asserts emitted total == smaller candidate AND strictly < what keeping RLE would produce;
  round-trips exactly.
- **Case B (sparse):** 3 isolated changes in 2000 bytes — best-of emits RLE (0x00); emitted ==
  5 + rleData; round-trips.
- **Case C (exact tie):** threshold set to `rleData/raw` so `rleData == raw × threshold` exactly →
  strict `>` is false → RLE (0x00) kept, verifying the lowest-mode-id tie-break direction.

### One consequential test fix (best-of is genuinely better)

`ApiAndConfigurationTests.BufferManagement_SpanAPI_BufferTooSmall` used a 5-byte input + 10-byte
buffer expecting "too small". Under best-of, that 5-byte input now correctly picks FullReplace
(5 header + 5 raw = 10 bytes) which FITS a 10-byte buffer, so 10 no longer exercises the too-small
path. Fixed by using a 4-byte buffer (< the 5-byte header — unconditionally too small for any mode).
This is a direct consequence of best-of choosing the smaller candidate, not a regression.

### Vector regeneration / validation alignment

`TestGenTests.DefaultOptions` had an explicit `CompressionThreshold = 1.5` used to VALIDATE the
regenerated vectors, but `DZ.TestGen.Program` generates them with `new DeltaOptions()` (defaults, now
1.0). Aligned the validation threshold to 1.0 so validation reproduces the generated `.delta.bin`
byte-for-byte (otherwise a near-threshold vector would generate one mode but validate as another).

## Verify — C# (real, --no-restore -m:1)

`dotnet test src/csharp/DeltaZorTests/DeltaZorTests.csproj --no-restore -m:1`:

```
Passed!  - Failed: 0, Passed: 117, Skipped: 4, Total: 121
```

- `AutoModeSelection_ChoosesBestCompressionMode` — now PASSES (un-skipped; skipped 5 → 4).
- The 4 remaining skips are pre-existing and unrelated (Checksum ×2, SevenBitEncoding ×2).
- `ValidateTestGenSamples` (all 56 vectors) — regenerates with default 1.0 and validates create +
  apply + round-trip byte-for-byte: PASS. No regression.

## Verify — Zig (real, under the installed 0.16.0 toolchain)

`zig build test` could not run because the committed Zig test harness (`tests.zig`,
`gen_testdata.zig`) targets **Zig 0.15.1** (used by TASK-0365), but the only toolchain installed here
is **Zig 0.16.0**, which removed/restructured the APIs the harness uses (`std.heap.
GeneralPurposeAllocator` → `std.heap.DebugAllocator`; `std.fs.cwd()` + `File.readToEndAlloc` →
`std.Io.Dir` + the new `std.Io` reader interface). This is **pre-existing toolchain drift, unrelated
to auto-mode logic** — confirmed: `tests.zig`/`gen_testdata.zig` are byte-unchanged from `master`
@645cf69 and the same errors reproduce at HEAD. The encoder/decoder/utils MODULES compile cleanly
under 0.16.

To verify the auto-mode parity crux FOR REAL under the actual toolchain, I wrote a 0.16-compatible
standalone parity harness (ArenaAllocator + `std.Io.Threaded` + `std.Io.Dir`) that reads the
C#-generated reference vectors (regenerated by the build from the current C# encoder, default 1.0)
and confirms Zig's production `createDelta`/`applyDelta` reproduce each C# delta byte-for-byte with
the SAME top-level mode, plus round-trip. Result over ALL 56 vectors:

```
modes across 56 vectors: RLE(0x00)=54 FullReplace(0x01)=2
PARITY-PROBE OK: all 56 vectors C#<->Zig same mode + byte-identical + round-trip
```

Both top-level modes are exercised (54 RLE, 2 FullReplace) and selected identically in both
languages — the same-mode-selection parity crux is confirmed. (The harness was a throwaway
verification tool, not committed; its output is captured above.)

### Follow-up (separate task, not TASK-0366)

`zig build test` needs the test harness migrated from Zig 0.15.1 to 0.16.0 (allocator rename +
`std.Io` file-I/O migration across ~33 call sites in `tests.zig` + `gen_testdata.zig`). This is a
toolchain-maintenance task independent of any compression logic and should be scheduled separately.

## Files changed

- `src/csharp/DeltaZor/DeltaZor.cs` — default `CompressionThreshold` 1.5 → 1.0; best-of docs.
- `src/zig/src/utils.zig` — default `compression_threshold` 1.5 → 1.0; mirrored docs.
- `src/csharp/DeltaZorTests/UnitTests/ArithmeticCompressionTests.cs` — un-skip + implement
  `AutoModeSelection` (3 cases).
- `src/csharp/DeltaZorTests/UnitTests/ApiAndConfigurationTests.cs` — too-small buffer 10 → 4.
- `src/csharp/DeltaZorTests/UnitTests/TestGenTests.cs` — validation threshold 1.5 → 1.0 (match
  generation).

## Outcome

EPIC-0046 capstone: the encoder now performs genuine auto-mode best-of at the top level (smaller of
RLE-delta vs FullReplace, deterministic lowest-mode-id tie-break), byte-parity-confirmed C#↔Zig
across all 56 vectors + round-trip, with the value gap (best-of beating the 1.5 heuristic) proven and
`AutoModeSelection` un-skipped and passing. GO.
