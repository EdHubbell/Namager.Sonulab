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

## 2. Paced-overlap serial pipelining (~1.7× on every bulk read)

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

## 3. Riding minor findings from the scan-fix reviews (small, adjudicated non-blocking)

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

## 4. Byte-exact preset restore/duplicate via dwrite (unbuilt option, protocol resolved)

PROTOCOL.md VERDICT 2026-07-04: preset content IS dwrite-able (chunk:0 name → 1..64 → name at
chunk:-1 commit, ~10 s/slot). Restore/duplicate today replay params (~12 s, not byte-exact).
A `dwrite`-based `BackupService.RestoreSlotAsync` would be byte-exact and slightly faster.
With pipelining (#2) the 66-write upload gets faster still.

## 5. Outstanding hardware-validation checklists (not code)

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
