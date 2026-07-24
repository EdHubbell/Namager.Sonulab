# Spec — Amp/IR slot reorder (Cycle 2)

**Date:** 2026-07-24
**Status:** Design approved (Ed, 2026-07-24). Ready for writing-plans.
**Prereq:** Cycle 1 (`docs/superpowers/specs/2026-07-24-dswap-reorder-core-design.md`) — merged. The
firmware `dswap` verb is hardware-confirmed on `root\amp` (~235 ms) and `root\ir` (~120 ms):
full name+content atomic swap, self-inverse (PROTOCOL.md verdict, 2026-07-24).

## Goal

Let the user reorder amp and IR slots from the Amps and IRs tabs, using the same up/down-button
interaction the Presets tab already has, built on the atomic `dswap` primitive. This completes the
"amp/IR reorder" feature deferred in CLAUDE.md "Not done".

## Scope & non-goals

- **In scope:** amp reorder AND IR reorder; the `dswap` primitive for the amp/IR block; a shared
  bubble-swap reorder engine; VM commands + item-level enable flags; view buttons; offline unit
  tests; hardware-validation checklist rows.
- **Non-goals:** drag-and-drop (mirror presets = buttons only; presets have no drag either);
  amp/IR *content* reorder beyond slot swap; multi-select reorder; retrofitting anything to presets
  beyond the shared-helper extraction below; `dmove` (unprobed).

## Background verified against the code (2026-07-24)

- `AmpService`/`IrService` are thin fronts over `SlotBlobService` (`_inner`), which owns the
  block-generic dread/dwrite/list on `_kind.ListPath` (`root\amp` 96 chunks / `root\ir` 32 chunks)
  and validates indices. They have no move/swap today.
- `AmpListViewModel`/`IrListViewModel` mirror each other and `PresetListViewModel`: a busy-gated
  `RunAsync(message, success, work)` that runs the work, `ReloadAsync`es, and surfaces failures
  without crashing. `AmpListViewModel.RunAsync` additionally drains any in-flight details read
  before a write burst (a full-slot dread overlapping a write can discard the commit).
- Preset reorder UI (`PresetListView.axaml`) = a toolbar Move-Up/Down on `Selected` + per-row
  reorder buttons bound to `MoveItemUp/DownCommand`, gated by `CanMoveUp/Down` on the item VM.
  No drag anywhere.
- The amp/IR `ISonuLink` test fake is `FakeSlotBlobDevice` (Name+Blob per slot, `SeedSlot`,
  `virtual SendAsync`, `DWriteRx`/`DReadRx` handlers) — no `dswap` handler yet.

## The two simplifications unique to amp/IR reorder

1. **No usage-map rescan, no `Invalidate()`, no targeted notify.** Presets reference amps/IRs by
   *name*; `dswap` moves name+content together, so reordering amp/IR slots never changes any
   preset→amp/IR reference. Reorder does the normal `ReloadAsync` (which re-applies the name-keyed
   "used in presets" highlights via `ApplyUsage`) and touches `_usage` not at all. (Contrast Cycle 1:
   preset reorder needed `NotifyPresetMoved` because the map keys on preset *slots*.)
2. **No "used by preset" guard on reorder.** Delete/rename are blocked when an amp/IR is referenced
   (they would orphan the preset's name reference — `BlockUsed`). Reorder preserves the name, so it
   is always safe: occupied *and* referenced amps/IRs reorder freely. Deliberate, correct asymmetry.

## Architecture — components (bottom-up)

### C1. `SlotBlobService.SwapAsync(int a, int b, ct)` — the amp/IR swap primitive

The block-generic twin of Cycle 1's `DeviceRepository.SwapPresetSlotsAsync`:

- Emit `dswap` on `_kind.ListPath` via the existing `SonuClient.DSwapAsync(path, a, b, ct)` (added
  in Cycle 1, already block-agnostic).
- Validate both indices in `[0, SlotCount)` before any I/O (non-numeric/out-of-range `dswap` index
  reboots the ESP32 — PROTOCOL.md hazard). Reuse the class's `_raise` for the error.

### C2. `SlotBubbleReorder` — the shared bubble-swap engine (extraction)

Extract the Cycle-1 `ReorderService` bubble/verify loop into one reusable helper so both preset and
amp/IR reorder share exactly one implementation (DRY; the logic is already review-approved):

- Static helper in `Sonulab.Core.Services`, parameterized on the block's list+swap:
  - `MoveAsync(int from, int to, Func<CancellationToken, Task<IReadOnlyList<string>>> readNames,
    Func<int, int, CancellationToken, Task> swap, IProgress<ReorderProgress>?, CancellationToken)`
  - `MoveStepAsync(int from, bool up, …same delegates…)`
- Behavior is byte-for-byte the current engine: a move `from`→`to` is `|from−to|` adjacent swaps;
  after each swap, re-read names and verify the two affected slots against a locally-tracked
  `expected[]`; on mismatch throw (no reverse-swap — `dswap` is atomic, a stopped move is a valid
  partial order). Boundary/empty/`from==to` handling unchanged.
- `ReorderProgress` record stays (moves to or stays accessible from the helper's file).
- **Refactor `ReorderService`** (presets) to delegate `MoveAsync`/`MoveStepAsync` to
  `SlotBubbleReorder`, passing `ct => (await _repo.ListPresetsAsync(ct)).Select(s => s.Name)…` and
  `_repo.SwapPresetSlotsAsync`. Its public API and all existing `ReorderServiceTests` are unchanged
  (this is the one justified touch to Cycle-1 code — it serves this feature by avoiding a
  duplicated engine).

### C3. `SlotBlobService` reorder methods

- `MoveStepAsync(int from, bool up, ct)` and `MoveAsync(int from, int to, ct)` delegate to
  `SlotBubbleReorder`, passing `ct => (await ListAsync(ct)).Select(s => s.Name)…` and `SwapAsync`.
  (`MoveAsync` is included for parity/testing; the UI only uses `MoveStepAsync`.)

### C4. `AmpService` / `IrService` fronts

- Each exposes `MoveStepAsync(from, up, ct)` and `MoveAsync(from, to, ct)` delegating to `_inner`,
  mirroring their existing `DeleteAmpAsync`/`RenameAmpAsync` delegation.

### C5. `AmpListViewModel` / `IrListViewModel` commands

Mirror `PresetListViewModel` exactly:

- `MoveUpAsync`/`MoveDownAsync` (act on `Selected`) and `MoveItemUpAsync`/`MoveItemDownAsync`
  (per-row), each computing `dest` and calling the service `MoveStepAsync` through the VM's existing
  `RunAsync` busy-gate (which already drains in-flight detail reads on the amp VM), then re-selecting
  the moved item at `dest`.
- **No `_usage` call** in the success path (§ simplification 1). Failure keeps `RunAsync`'s existing
  surface-and-stay-alive behavior; no `Invalidate()` needed since reorder never affects the map.
- Boundary guards mirror presets (`Index > 0` for up, `Index < SlotCount - 1` for down; occupied
  only).

### C6. `AmpItemViewModel` / `IrItemViewModel` enable flags

- Add `CanMoveUp`/`CanMoveDown` (occupied and not at the list boundary), mirroring
  `PresetItemViewModel`, for the per-row button `IsEnabled` bindings.

### C7. Views — `AmpListView.axaml` / `IrListView.axaml`

- Add the toolbar Move-Up/Down buttons and the per-row reorder buttons + `Button.reorder` style,
  copying `PresetListView.axaml`'s markup and bindings (`MoveUp/DownCommand`,
  `MoveItemUp/DownCommand`, `CanMoveUp/Down`). Use existing theme tokens/icons (no hardcoded hex).

## Testing strategy

- **`FakeSlotBlobDevice`** gains a `dswap` handler (regex `^dswap (\S+):\{"index":A,"index2":B\}$`)
  that swaps two slots' Name AND Blob atomically — the Cycle-1 `FakePresetDevice` pattern adapted to
  the blob fake. Reject nothing extra (index validation lives in `SlotBlobService`).
- **`SlotBlobService.SwapAsync`** tests: swaps name+content; swap-with-empty moves+empties;
  out-of-range throws.
- **`SlotBubbleReorder`** tests (block-agnostic, via delegates over the blob fake or a light stub):
  single-step move (occupied + empty neighbor) = one swap; multi-slot bubble matches expected order
  including interior empties; verify-failure → throw leaving a valid partial order. (The preset
  `ReorderServiceTests` continue to cover the same engine through the delegation.)
- **Amp/IR VM tests:** reorder success reloads and reorders `Items`, re-selects `dest`, and makes
  **zero** `_usage` calls (assert via a fake usage service: `InvalidateCount == 0`, no notify);
  reorder is ALLOWED on a preset-referenced amp/IR (not blocked); a failed reorder surfaces an error
  without crashing and without touching `_usage`.
- Full suite stays green.

## Hardware validation (after merge)

Add rows to `docs/HARDWARE-VALIDATION-amps-tab.md` (and an IR equivalent):

- Reorder an amp up/down on the Amps tab; confirm order + names + content correct on the pedal, fast
  (~235 ms/step), and that a *referenced* amp reorders without being blocked and its presets still
  resolve it (name unchanged).
- Same on the IRs tab (~120 ms/step).
- Confirm the "used in presets" highlights are unchanged by a reorder (no rescan/flicker).

## Key references

- `docs/superpowers/specs/2026-07-24-dswap-reorder-core-design.md` — Cycle 1 (the engine being shared).
- `PROTOCOL.md` — `dswap` verdict (amp/IR confirmed, active-slot safe).
- `src/Sonulab.Core/Services/SlotBlobService.cs` — blob primitives (gets `SwapAsync` + reorder).
- `src/Sonulab.Core/Services/ReorderService.cs` — the bubble engine being extracted/shared.
- `src/Namager.App/ViewModels/AmpListViewModel.cs`, `IrListViewModel.cs`,
  `PresetListViewModel.cs` — the VM pattern being mirrored.
- `src/Namager.App/Views/PresetListView.axaml` — the reorder-button markup to copy.
- `tests/Sonulab.Core.Tests/FakeSlotBlobDevice.cs` — the fake gaining a `dswap` handler.
