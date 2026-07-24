# Handoff — preset-usage highlight scan is far too slow on device

**Date:** 2026-07-24
**Branch:** `feat-preset-usage-guard` (built on `fix-connect-reconnect`, which is on `main` tip)
**Branch tip:** `6e1e029` · **Tests:** 619/619 green · **NOT merged** (was awaiting on-device validation)

## TL;DR — TWO bugs found

The "which presets use this amp/IR" feature passed all task/final review (619/619 tests) but does
not work on real hardware, for two independent reasons:

1. **PERF (the reported symptom):** the first-time usage scan is unusably slow — opening the Amps
   tab shows **"Checking preset usage…"** and never finishes (user saw 30s+). Confirmed **design
   flaw**, not a code bug: the scan reads every occupied preset's full 8192-byte document over
   serial, and one full preset read is **~3–7 s**, so a full bank takes **minutes**. The spec/plan
   assumed "a few seconds" — off by ~50×. Trimming the read to the front of the document helps
   ~2.5× but is not enough alone (see evidence below).

2. **CORRECTNESS (latent, would surface once perf is fixed):** `PresetUsageMap.Build` extracts
   amp/IR references by the schema **`ref`** field (`root\amp`/`root\ir`), but real preset
   documents read via `dread`/`.pst` contain only `{"value":…}` lines with **no `ref`** — so the
   map comes back **empty** and **nothing ever highlights**, even after the slow scan completes.
   The unit tests are green only because their fixtures inject a synthetic `"ref"` the device never
   sends. Fix = match by **node path** (`root\app\amp\amp`, `root\app\ir\ir`, `root\app\ir\ir2\ir`)
   instead of by schema `ref`. **Verify against a real `.pst` first** (they're on disk — see below).

Both need fixing before the feature works. The perf fix is an **architecture/UX decision** (Options
below) — confirm the approach with the user (Ed) before building, per systematic-debugging Phase
4.5 (question the architecture, don't keep patching).

## Symptom (as reported)

> "The first time I opened the amp list, it says 'checking preset usage'. No results after over
> 30 seconds."

## Root cause (confirmed — Phase 1 complete)

The highlight requires knowing, for every preset, which amp/IR it references. Each preset
reference lives inside the preset's 8192-byte document. Reading one document =
`DeviceRepository.ReadPresetAsync` → `SonuClient.DReadBlobAsync(path, index, 64)` =
**64 sequential `dread` serial round-trips** (`src/Sonulab.Core/SonuClient.cs:128-148`,
`src/Sonulab.Core/Services/DeviceRepository.cs:37-41`, `PresetChunks = 64`).

`PresetUsageService.GetAsync` (`src/Namager.App/Services/PresetUsageService.cs`) loops over **every
occupied preset** and calls `ReadPresetAsync` for each — strictly sequential (each await blocks the
next), all on the one serial `SonuClient`.

### Evidence for per-read cost (`docs/perf-findings.md`)
- Old reorder toolbar backup, which reads **one** preset's content to back it up, measured
  **backup = 7312 ms** (`perf-findings.md:17,45`). That ~7s is dominated by the 64-chunk read.
- Amp full-slot read (96 chunks, 12288 B) is quoted as "~5 s" in `AmpListViewModel` comments
  → ~52 ms/chunk → a 64-chunk preset ≈ **~3.3 s** even on the optimistic estimate.
- So scanning ~20–30 occupied presets ≈ **60–210 seconds**. Entirely consistent with "30s+ and
  still going." It is **not hung** — it is grinding through sequential reads.

### Why the UI appears frozen
`AmpListViewModel.RefreshAsync` holds `IsBusy` for the whole load; `ReloadAsync` populates
`Items` first, then `await ApplyUsageAsync()` → `_usage.GetAsync()` opens the status scope
**"Checking preset usage…"** and blocks there. The ListBox is `IsEnabled="{Binding !IsBusy}"`, so
the list is greyed and the status line sticks until the multi-minute scan finishes.

## Node-position evidence (RESOLVED — favorable, but only ~2.5×)

Real captured preset documents exist on disk: **16 `.pst` files in
`docs/backups/probe-20260616-073226/`** (device probe, 2026-06-16), each 8192 B. Also
`docs/backups/preset-probe-20-...-deleted.bin` (raw 8192 B blob) and shipped `presets/*.pst`.
Use these to build a **realistic** test fake (the current `FakePresetDevice` just CRLF-joins
seeded lines into a zeroed buffer — feed it real `.pst` lines).

Measured from `docs/backups/probe-20260616-073226/00 - Quad Reverb SM57.pst`:
- Content = **7046 B** of 8192 (only ~1.1 KB trailing NUL) → **stopping at the content-end NUL
  barely helps**; the document is nearly full.
- Nodes are in a **fixed self-describing schema order** (stubs first: gate/exp/comp/amp/eq/ir/…,
  then each block's params), NOT scattered. Reference-node positions:

  | Node (the reference value we need) | line | byte | chunk (of 64) |
  |---|---|---|---|
  | `root\app\amp\amp` (amp name) | 26 | 883 | **chunk 7** |
  | `root\app\ir\ir` (primary IR name) | 37 | 1322 | **chunk 11** |
  | `root\app\ir\ir2\ir` (secondary/dual IR name) | 74 | 2859 | **chunk 23** |
  | last IR-block line (`…\ir2\…pan2`) | 80 | 3117 | **chunk 25** |

- **To capture amp + ALL IR refs: read chunks 1..~25** (≈40% of the doc). amp + *primary* IR only:
  chunks 1..11. Offsets drift slightly with value lengths, so read adaptively — stop once the text
  of the **next block (`root\app\mod`)** has been seen — with a safety cap (e.g. chunk 32).

**Implication:** trimming cuts per-preset reads **64 → ~25 (~2.5×)**, i.e. ~1.3–3s/preset instead
of ~3.3–7s. For a full bank (~20–30 presets) that's still **~30–90s** — **better, but not enough by
itself for a blocking on-tab-open scan.** Trimming must be combined with a non-blocking/progressive
approach (Options A+B/D below). There is no multi-chunk `dread` (one 128-B chunk per serial
round-trip, ~52 ms each — see `SonuClient.DReadChunkRangeAsync`), so ~25 round-trips/preset is the
floor for full IR coverage.

**Note:** `ref":"root\\ir"` is a *browse-schema* field, NOT in the `.pst`. In the stored document
the IR references are the value lines `root\app\ir\ir` and `root\app\ir\ir2\ir`. `PresetUsageMap`
currently keys off the schema `ref` (`root\amp`/`root\ir`) which IS present when the map is built
from a live `browse`/editor read — **but the `.pst`/`dread` document lines carry only `{"value":…}`
with no `ref`.** ⚠️ **This is a second latent bug:** `PresetUsageMap.Build` extracts refs via
`NodeSchema.FromRecord(rec).Ref`, which will be **null** for every line in a real preset document
read via `dread`, so the map would come back **empty** even after the slow scan finishes. The next
agent MUST verify this against a real `.pst` and, if confirmed, switch extraction to **match by
node path** (`root\app\amp\amp`, and any `root\app\ir…\ir` / `…\ir2\ir`) instead of by schema
`ref`. (The existing unit tests pass only because their fixtures inject a synthetic `"ref"` field
that the real device does not send.)

**Tooling:** `tools/HwCheck` has a read-only `--dread-probe <path> <index> <chunk...>` dumper
(`Program.cs:177-206`) — e.g. `dotnet run --project tools/HwCheck -- --dread-probe root\presets 0 1 2 3 4 5 6 7 8 9 10 11`
prints raw hex+ASCII of a live preset's amp/IR region. No batch `--dump-presets` exists (only
`--dump-amps`/`--dump-irs`).

## 2026-07-24 protocol probe results (HwCheck `--dread-arg-probe` / `--pipeline-probe` / `--dswap-probe`, fw 2.5.1 serial)

Ed asked whether the firmware exposes a cheaper read path. Probed live; also swept VoidX's
`app.so` string pool (the source of the original protocol RE). Full detail now in PROTOCOL.md.

1. **No batch read exists.** The complete VoidX command vocabulary is read/browse/write/dread/dwrite
   (+ two undocumented verbs below). `dread` ignores every extra numeric arg (`count`/`size`/…) —
   one 128-B chunk per round trip is a hard firmware limit. ⚠️ A **non-numeric `chunk` value
   crashes the firmware** (abort + ESP32 reboot) — never send one.
2. **Paced overlap works (the "batch read" substitute):** the firmware drops zero-gap pipelined
   commands, but accepts the next command while streaming the previous response. **30 ms send pace
   sustained 16/16 chunks at ~32.6 ms/chunk vs ~57 ms lockstep (~1.75×)**; 25 ms pace drops
   commands. Production should self-clock (send next on first response bytes), not hard-code 30 ms.
   → Windowed (~14–16 chunks) + paced ≈ **~0.5 s/preset ≈ ~14 s full bank** — background-scan
   territory. Lockstep windowed alone ≈ 0.8–0.9 s/preset ≈ ~25 s.
3. **Bonus (reorder, not scan): `dswap` verb confirmed.** `dswap root\presets:{"index":A,"index2":B}`
   swaps two slots' name+content **byte-verified in ~213 ms** — vs ~1.5 s/step select+save. Found in
   `app.so` strings; probed guarded (backup + swap-back). A `dmove ` string also exists — unprobed.
   Rebuilding ReorderService on dswap (and probing dswap on `root\amp`/`root\ir` for the deferred
   amp/IR reorder) is a separate follow-up.

**Implication for the options below:** A (trim/window) + B (background/progressive) remains the
architecture; the paced-overlap transport trick roughly halves the residual scan time. There is
no firmware escape hatch that makes a blocking on-tab-open scan viable.

## Fix options (architecture decision — confirm with Ed before building)

The original design chose "lazy scan on tab open + cache" over "on-demand at delete only," when we
both believed the scan was "a few seconds." That premise is now false, so the choice should be
revisited.

- **A. Trim the read.** Read presets incrementally, stop at the content-end NUL and/or as soon as
  the amp value + all IR values are captured. Best case (nodes early, short docs): scan drops to
  ~10–20 s. Still a blocking-ish wait but tolerable. Viability depends on the open investigation.
  Requires a new lean read path (don't reuse the 64-chunk `ReadPresetAsync`).
- **B. Background + progressive + write-safe.** Show the amp/IR list immediately (do NOT gate it on
  the scan). Run the scan in the background and fill highlights in as each preset resolves. MUST
  stay off the serial link while a user-initiated read/write runs (the shared-link hazard we just
  fixed in `6e1e029` — see the busy-gate on `RefreshUsageAsync`). The delete/rename guard needs a
  ready map: if not ready, either await a fast (trimmed) targeted check or degrade safely.
- **C. Manual "Check usage" button.** Don't scan automatically. Highlight/guard only after the user
  explicitly asks (one visible slow operation they opted into), then cache. Cheapest to build,
  honest about the cost, no surprise hang.
- **D. A + B combined** (trimmed lean read, run in background, progressive fill, cooperative with
  writes). Best UX, most work.
- **E. Drop or defer the highlight; keep only the delete/rename guard**, computed on-demand at click
  time (still slow at click, but only pays the cost when the user actually deletes/renames). Loses
  the always-on highlight the feature was about.

**Controller's lean:** A+ (trim) is the highest-leverage single change if the node evidence is
favorable; combine with B (don't block the list; background/progressive) so the tab is usable
instantly regardless. If the evidence is unfavorable (nodes scattered, docs long), C (manual
trigger) is the honest fallback. Get Ed's call.

## Constraints any fix MUST honor

- **Serial-link safety (already bitten once):** the usage scan shares the one `SonuClient` with
  amp/IR reads/writes. Overlapping a dread burst with a write burst is a documented data hazard
  (HwCheck finding; see comments in `AmpListViewModel.RunAsync`). The final review already caught
  and fixed an ungated `RefreshUsageAsync` (commit `6e1e029`). Any background scan must yield the
  link to user-initiated operations, never interleave.
- **Guards stay correct:** delete/rename of a used file must remain blocked; do not let a
  not-yet-ready map allow deleting a referenced file (the exact dangling-ref the feature prevents).
- **Best-effort highlight:** a preset-read failure must never break the amp/IR list (current
  `ApplyUsageAsync` swallows+logs — keep that).
- Core stays UI-free; theme tokens only in `.axaml`; new ctor params optional (`=null`).

## Key code locations

| What | Where |
|---|---|
| Slow scan (loops all occupied presets) | `src/Namager.App/Services/PresetUsageService.cs` `GetAsync` |
| Full 64-chunk preset read | `src/Sonulab.Core/Services/DeviceRepository.cs:37-41` (`ReadPresetAsync`) → `SonuClient.cs:128-148` (`DReadBlobAsync`/`DReadChunkRangeAsync`) |
| Pure name→presets index | `src/Sonulab.Core/Services/PresetUsageMap.cs` (`Build`, `PresetRef`) |
| Amp list apply/guards | `src/Namager.App/ViewModels/AmpListViewModel.cs` (`ApplyUsageAsync`, `RefreshUsageAsync`, `DeleteAsync`, `CommitRenameAsync`, `BlockUsed`) |
| IR list apply/guards | `src/Namager.App/ViewModels/IrListViewModel.cs` (mirror) |
| Cache invalidation on preset edit | `src/Namager.App/ViewModels/PresetListViewModel.cs` `RunAsync` |
| Shared service wiring + tab-revisit refresh | `src/Namager.App/ViewModels/MainWindowViewModel.cs` (`EnsureTabLoaded`, `Connected` handler) |
| Item highlight props | `AmpItemViewModel.cs` / `IrItemViewModel.cs` (`UsedInPresets`/`IsUsed`/`UsedInTooltip`) |
| Views (highlight + tooltip) | `src/Namager.App/Views/AmpListView.axaml`, `IrListView.axaml` |
| Status "Checking preset usage…" scope | `PresetUsageService.GetAsync` `_status.BeginOperation(...)` |

## Reference docs

- Spec: `docs/superpowers/specs/2026-07-23-preset-usage-guard-design.md`
- Plan: `docs/superpowers/plans/2026-07-23-preset-usage-guard.md`
- On-device checklist: `docs/HARDWARE-VALIDATION-preset-usage.md`
- SDD ledger (full task/review/fix history): `.superpowers/sdd/progress.md` (search
  "preset-usage-guard"; gitignored)
- Perf numbers: `docs/perf-findings.md`
- Wire protocol (dread/preset layout): `PROTOCOL.md`

## Suggested next steps for the picking-up agent

1. **Confirm & fix bug 2 (empty-map) first — it's cheap and blocks everything.** Read a real
   `.pst` (`docs/backups/probe-20260616-073226/*.pst`) and confirm the lines carry no `"ref"`.
   Then change `PresetUsageMap.Build` to match by **node path** (amp = `root\app\amp\amp`; IR =
   any `root\app\ir\ir` or `root\app\ir\ir2\ir`, i.e. an `ir` leaf under the `ir` block) rather
   than schema `ref`. **Rewrite the unit-test fixtures to use REAL `.pst` node lines** (no
   synthetic `ref`) — ideally seed `FakePresetDevice` from an actual `.pst` file so the tests
   exercise real data. This is a TDD fix (RED with real lines against current `Build` → GREEN).
2. Use **superpowers:systematic-debugging** for the perf issue — root cause is already Phase-1
   complete and the node-position evidence is gathered (both documented above); do NOT re-litigate,
   move to Phase 2/3 on the chosen approach.
3. Bring Ed the root cause + Options A–E and get his pick (this is a UX contract change, not a
   silent patch). Likely a short **superpowers:brainstorming** on approach, then
   **superpowers:writing-plans** for the chosen fix. Current lean: **trim the read to the amp/IR
   region (~chunks 1..~26, adaptive stop at `root\app\mod`) AND make the scan non-blocking +
   progressive + write-safe** (Options A+B/D) — trim alone (~2.5×) still leaves a ~30–90 s scan.
4. Whatever is built: add a test that proves the scan cost is bounded (a fake that counts `dread`
   commands and asserts a used-highlight resolves within ~26 chunks/preset, and that the list is
   never blocked on the scan), and re-run the full suite (619 baseline) + the on-device checklist.
5. Keep the SDD ledger updated if continuing under subagent-driven-development.
