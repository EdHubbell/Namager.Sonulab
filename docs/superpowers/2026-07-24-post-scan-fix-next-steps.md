# Handoff — proposed next steps after the preset-usage scan fix

**Date:** 2026-07-24
**State:** `main` = `e22c314`, 648/648 tests green, pushed. The preset-usage scan fix
(plan `docs/superpowers/plans/2026-07-24-preset-usage-scan-fix.md`) is **merged and
on-device validated by Ed** ("Works OK", 2026-07-24). The `feat-preset-usage-guard`
branch is fully merged (fast-forward) and can be deleted.

Everything below is a PROPOSAL — nothing here is committed work. Ranked by leverage.
Each item should get its own brainstorm→spec→plan cycle per the project workflow.

## 1. dswap-based reorder engine (biggest UX win, protocol already proven)

The undocumented firmware verb `dswap <path>:{"index":A,"index2":B}` atomically swaps two
slots — **name AND content, byte-verified, in ~213 ms** (confirmed live 2026-07-24, fw 2.5.1,
`tools/HwCheck --dswap-probe`; documented in PROTOCOL.md "Commands"). The current
`ReorderService` does ~1.5 s/step select+save with a unique-temp-name dance.

- Rebuild `ReorderService.MoveStepAsync`/`MoveAsync` on dswap: any permutation = a sequence of
  swaps; a single-step move = one command (~7× faster), no temp names, no save-by-name
  uniqueness precondition, no live-preset clobbering (probe showed content+name move together).
- Verify first on-device: does dswap disturb the CURRENTLY SELECTED preset if one of the two
  slots is active? (Probe used non-active slots. Add a HwCheck check before building.)
- Probe `dswap` on `root\amp` and `root\ir` (extend `--dswap-probe` with a `--path` arg + the
  amp/IR restore path). If it works there, the **deferred amp/IR reorder feature**
  (CLAUDE.md "Not done") becomes nearly free.
- `dmove ` also exists in VoidX's app.so string pool — semantics unknown (move-with-shift?).
  Probe ONLY with a full-bank backup; a wrong guess about shift semantics scrambles slots.

## 2. Paced-overlap serial pipelining (~1.7× on every bulk read) — BUILT 2026-07-24, hardware validation pending

**Status:** implemented on `worktree-feat-serial-pipelining` (transport + `SonuClient`
foreground bulk read only). Manual on-device checks: `docs/HARDWARE-VALIDATION-pipelining.md`.
**Deferred follow-up:** the usage scan is NOT accelerated. `DeviceRepository.ReadPresetHeadAsync`
requests one chunk per call so it can stop as soon as the amp/IR refs are complete; batching it
means grouping requests and over-reading up to `group-1` chunks past that stop point, and it
touches the scan path. Worth doing once the usage-map work has landed — a group of 4 would take
the scan from ~14 s to ~8 s.

Probe-proven (2026-07-24, `--pipeline-probe`): the firmware drops zero-gap pipelined commands
but accepts the next command while still streaming the previous response — a ≥30 ms send pace
sustained ~32.6 ms/chunk vs ~57 lockstep; 25 ms is the cliff. PROTOCOL.md "dread limits &
hazards" has the numbers.

- Implement as a transport-level option (SerialSonuLink or a SonuClient bulk-read path) that
  **self-clocks** — send command N+1 when the first bytes of response N arrive — rather than
  hard-coding 30 ms.
- Beneficiaries: usage scan (~14 s → ~8 s full bank), amp dumps (`--dump-amps` 96 chunks/slot),
  backups (SnapshotAllAsync), the preset-dwrite byte-exact restore path.
- Serial only (TCP already has no meter stream and different latency; probe separately if wanted).
- Care: keep the background-lane quiet-window semantics intact; pipelining is WITHIN one
  burst, the lane governs BETWEEN bursts.

## 3. Targeted usage-map updates on preset slot changes (Ed-requested, 2026-07-24)

Today `PresetListViewModel.RunAsync` calls `_usage.Invalidate()` after EVERY successful preset
mutation, so a mere reorder/slot move triggers a full ~15–30 s background re-scan — even though
a slot change moves content without altering which amps/IRs are referenced. **A successful slot
change should just remap slots on the in-memory map, not rescan.**

- Add pure transforms to `PresetUsageMap` (it's immutable — return a new map):
  `WithSwappedSlots(a, b)`, and while in there the same family covers the other cheap cases:
  `WithRenamedPreset(index, newName)`, `WithoutSlot(index)` (delete).
- Add targeted notifications to `IPresetUsageService` (e.g. `NotifySlotsSwapped(a, b)`,
  `NotifyPresetRenamed(index, name)`, `NotifyPresetDeleted(index)`) that transform `Current`
  in place, KEEP `IsComplete` true, and raise `MapUpdated` — then have the preset VM call the
  targeted one instead of `Invalidate()` for reorder/rename/delete successes.
- Apply ONLY on verified success (the reorder path read-back-verifies; on failure/rollback keep
  today's `Invalidate()` as the safe fallback).
- Caution with the CURRENT select+save reorder engine: one visible "step" is internally several
  rename/save operations with temp names — apply the map transform once per completed verified
  step, not per internal sub-operation. With the dswap engine (#1) this becomes trivial: one
  atomic device swap ↔ one `WithSwappedSlots(a, b)`. Build these two together.
- Content edits (param save, amp/IR selection change) still need a rescan — but a natural
  extension is a single-slot targeted rescan (`Invalidate(index)` re-reading one preset head,
  ~0.5 s) instead of the full-bank invalidate. Also fixes the rescan highlight flicker (see #4)
  for the common cases.

## 4. Riding minor findings from the scan-fix reviews (small, adjudicated non-blocking)

Recorded in `.superpowers/sdd/progress.md`; none block anything:
- **Invalidate→rescan highlight flicker**: after a preset edit + tab revisit, highlights blink
  off and refill (~1–2 s blank, then progressive). Ed validated as-is; if it starts to annoy,
  publish merged-into-stale maps instead of rebuilding from empty (`PresetUsageService.RunScanPassAsync`).
- `PresetUsageMap.HeadComplete` uses first-`}` detection, not JSON balance — a `}` inside one of
  the three ref values could false-positive the head-read stop. Names cap ~31 chars; low risk.
- `ReadPresetHeadAsync`: per-chunk torn check is `seg.Length == 0`; `!= 128` would be stricter.
  Also re-decodes the accumulated buffer per chunk (O(n²) over ≤4 KB — cosmetic).
- `IrListViewModel.RunAsync` doc comment doesn't note the intentional no-details-drain
  divergence from `AmpListViewModel.RunAsync`.
- No reconnect unit test for scanner lifetime (MainWindowViewModel lacks a DeviceSession
  injection seam — pre-existing; a seam refactor would enable it).
- AmpListViewModelTests: `MakeUsageVm` near-duplicates `MakeWithUsage`.

## 5. Byte-exact preset restore/duplicate via dwrite (unbuilt option, protocol resolved)

PROTOCOL.md VERDICT 2026-07-04: preset content IS dwrite-able (chunk:0 name → 1..64 → name at
chunk:-1 commit, ~10 s/slot). Restore/duplicate today replay params (~12 s, not byte-exact).
A `dwrite`-based `BackupService.RestoreSlotAsync` would be byte-exact and slightly faster.
With pipelining (#2) the 66-write upload gets faster still.

## 6. Outstanding hardware-validation checklists (not code)

Pending manual runs, per CLAUDE.md "Not done": amp metadata
(`docs/HARDWARE-VALIDATION-amp-metadata.md` — run before relying on SSMD blocks on-device),
UI-polish visual checklist, Tone3000 live checklist, amps-tab + drag-reorder checks.

## Protocol hazards any future device work must respect (learned this session)

- **Never send a non-numeric `dread` chunk value** — firmware `abort()`s, ESP32 reboots
  (PROTOCOL.md). `--dread-arg-probe` gates these variants behind `--include-crash-variants`.
- No batch read exists; VoidX's complete JSON key vocabulary is `index`, `chunk`, `value`,
  `index2`, `save` (exhaustively enumerated from app.so strings).
- Serial commands are NOT queued by the firmware (zero-gap bursts drop); TCP queues but answers
  late (existing owed-response handling in TcpSonuLink).

## Key references

- PROTOCOL.md — dswap/dmove verbs, dread limits & hazards, pipelining pace numbers
- docs/superpowers/2026-07-24-preset-usage-scan-perf-handoff.md — root cause + probe evidence
- docs/superpowers/plans/2026-07-24-preset-usage-scan-fix.md — the merged fix's plan
- .superpowers/sdd/progress.md — full task/review ledger (gitignored)
- tools/HwCheck — `--dread-arg-probe`, `--pipeline-probe`, `--dswap-probe` harness modes
