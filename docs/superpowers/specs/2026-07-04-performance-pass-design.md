# Performance Pass — connect time, reorder, and the preset-dwrite verdict

**Date:** 2026-07-04
**Status:** Approved
**Context:** last of the three follow-up sub-projects (editor polish → IR tab → **this**).
Pain points named by Ed: **Connect** and **Reorder**. The pedal is available; measurement and
probe tasks run inline.

## Goal

Measurably faster Connect and toolbar reorder, plus a settled, timed verdict on PROTOCOL.md's
OPEN QUESTION (preset content via dwrite with the correct name-at-`chunk:-1` commit — the
2026-06-15 "not dwrite-able" conclusion used the buggy zeros terminator, the same bug that broke
amp uploads). Every change is bracketed by before/after hardware timings in `docs/perf-findings.md`.

## Scope (decided in brainstorming)

**In:** connect-phase baseline instrumentation; the guarded+timed preset-dwrite probe; lazy
Amps/IRs tab loading; adaptive port settle (build only if the baseline confirms the fixed
1500 ms settle dominates — it is expected to); toolbar Move Up/Down switched to the lean
`MoveStepAsync` path; after-measurements + docs.
**Out (not Ed's pain, recorded as follow-ups if the probe passes):** applying dwrite to
restore/duplicate/rollback paths; editor-load optimization; duplicate-speed work; any UI changes
beyond the invisible loading change.

## Design

### 1. Baseline measurement (hardware)

A connect-phase breakdown logged once per connect at Info level (NLog per-command Trace timings
already exist): port open → settle → probe(s) → compatibility check → preset list → editor
browse → amps list → IRs list, each with elapsed ms and a total. Plus timed toolbar-reorder runs
(adjacent occupied swap and relocate-into-empty). Captured on the pedal BEFORE any fix and
recorded in `docs/perf-findings.md`.

### 2. The preset-dwrite probe (hardware, guarded, timed)

HwCheck `--preset-dwrite-probe`: dread an occupied preset's 64 chunks (source stays untouched),
dwrite them into an EMPTY preset slot using the amp/IR sequence (`chunk:0` name → `chunk:1..64`
content → `chunk:-1` name = commit), read-back verify byte-equality, report per-phase timings,
then delete the probe slot. WritesAllowed-gated; the probe touches only an empty slot.
Every outcome is a valid verdict (not a STOP gate):
- **works + fast** → PROTOCOL.md verdict updated; follow-up recorded: byte-exact dwrite
  restore/duplicate/rollback (own sub-project; out of scope here).
- **works + slow** → verdict documented with timings; select+save remains the copy engine;
  question closed.
- **still refuses** → the 2026-06-15 conclusion stands on its own merits; OPEN QUESTION flag
  removed from PROTOCOL.md.

### 3. Lazy Amps/IRs tab loading

`MainWindowViewModel` still constructs the Amp/IR VMs on `Connected`, but their initial
`RefreshCommand` no longer fires there. The nav handler (`MainWindow.axaml.cs`
`OnNavSelectionChanged`) notifies the VM (`EnsureTabLoadedAsync(index)`), which fires each tab's
first refresh exactly once (subsequent visits use the existing manual Refresh button). Connect
loses two full 30-name list reads plus their serial round-trips; the Presets tab (landing tab)
stays eager. Behavior change is invisible except: switching to Amps/IRs the first time shows the
list appear then (with the existing busy indicator) rather than being preloaded.

### 4. Adaptive port settle

`SerialSonuLink`'s fixed `OpenSettleMs` (1500 ms — ESP32 reboots when the port opens) becomes an
early-probe poll: after open, attempt the existing probe on a short interval (~250 ms) within the
SAME total worst-case budget, proceeding on the first valid response. Boot-time garbage on the
line is tolerated by the existing probe/parse machinery (a garbage response = failed attempt =
wait and retry). Timeout behavior and error messages are unchanged when the device never
responds. Built only if the baseline confirms settle+probe dominates connect; if measurement
surprises us (e.g. the compatibility check dominates), that finding goes to Ed before building
anything unplanned.

### 5. Toolbar reorder → lean path

`PresetListViewModel.MoveUpCommand`/`MoveDownCommand` switch from `_reorder.MoveAsync(from, to)`
(which backs up the affected range by reading full preset content — the slow part) to
`_reorder.MoveStepAsync(from, up:)`, the same lean content-free path the per-row arrows use.
Since drag-reorder was removed, single steps are the only reachable move; `MoveAsync` remains as
`SwapAdjacentAsync`'s device-full fallback and for any future multi-slot use. Selection-follow
behavior in the VM is preserved.

### 6. After-measurement + docs

Re-run the same hardware timings; before/after table in `docs/perf-findings.md`. PROTOCOL.md
gains the probe verdict (and loses the OPEN QUESTION flag). CLAUDE.md updated (HwCheck flag,
test count). No hardware-validation checklist additions — the measurements ARE the validation.

## Error handling

No new user-facing failure modes: lazy loading reuses the tabs' existing error surfaces; the
adaptive settle preserves existing timeout/incompatible-device messages; the toolbar swap
inherits `MoveStepAsync`'s phase-aware recovery (already hardware-hardened); the probe is
guarded like every HwCheck write path.

## Testing

- Lean-path toolbar: `PresetListViewModelTests` updated — Move Up/Down asserts the step behavior
  (already modeled by `FakePresetDevice`) and that no content dread occurs on the toolbar path.
- Lazy tabs: `MainWindowViewModel` gains testable `EnsureTabLoadedAsync`; tests assert amps/IRs
  refresh does NOT run at Connected and DOES run exactly once on first visit.
- Adaptive settle: unit test with a fake link that answers the probe only after N attempts;
  asserts early success, budget cap, and unchanged failure mode.
- Probe: covered by SlotBlob-style unit tests only insofar as it reuses existing service calls;
  its real test is the hardware run, whose output lands in the report + docs.
- Full suite green (~271 today).

## Definition of done

`docs/perf-findings.md` shows a before/after table from the real pedal with Connect measurably
faster (lazy tabs + settle change) and toolbar moves running the lean path (~1.5 s single step,
no content reads). PROTOCOL.md's OPEN QUESTION is replaced by a timed verdict either way. Full
suite green.
