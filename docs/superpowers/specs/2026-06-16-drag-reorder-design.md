# Drag-to-Reorder + Fast Select+Save Engine — Design Spec (Feature B)

Status: approved in brainstorming 2026-06-16, informed by an on-device probe. Builds on
`SlotPlanner`, `ReorderService`, `DeviceRepository`, `BackupService`, and the `PresetListViewModel`.

## Goal
Let the user **drag a preset to a new slot** (with a clear drop indicator), and make reorder fast by
replacing the param-replay write mechanism with **select+save**.

## Probe findings (2026-06-16, hardware, backed up + restored)
- Writing a reordered name array to the `root\presets` list node had **no effect** — there is **no
  native one-command reorder**.
- **`select preset by name` → `save to slot` took ~216 ms** with byte-identical content (the device
  copies the 8 KB internally). The current param-replay is ~12,000 ms. → **~55× faster.**

## Engine: rewrite `ReorderService.MoveAsync` to use select+save
A move from→to is a **rotation of the contiguous range `[min,max]`**. Apply it in place using
select+save and **one temp slot**, so no slot's content is overwritten before it's been moved:

1. **Backup** the affected range (`[min,max]`) to `.pst` (read-back source for verify/rollback).
2. **Acquire a temp slot:** the lowest-indexed empty slot (any index). If none is free, fall back to
   the dread+param-replay path for the single displaced preset (documented slow fallback) — or
   surface "free a slot to reorder when full".
3. **Rotation (moving up, from > to):**
   - Save preset@`from` → temp slot (select its name, save under a unique temp name).
   - For `k = from` down to `to+1`: slot `k` ← preset@`k-1` (select name@`k-1`, save to slot `k`
     under name@`k-1`). Top-down order guarantees each source is read before it's overwritten.
   - Slot `to` ← temp (select temp name, save to `to` under preset's original name); clear temp slot.
   - (Moving down, from < to, is the mirror: save@from→temp, shift `k=from..to-1` up, temp→to.)
   - Track each slot's current name in memory (deterministic from the plan) so "select name@k" is
     always correct as names shift.
4. **Verify:** re-list names == expected order; spot read-back the moved preset's content == backup.
5. **Rollback** on any failure: restore the affected range from the `.pst` backup (via the existing
   write-to-slot path), then rethrow (wrapped so the original error isn't masked).

Cost: ~`(|from-to|)+2` saves × ~216 ms ≈ ~6 s for a 30→1 drag (vs ~6 min). `SlotPlanner` (pure
rotation math + changed range) is reused; only the *write* mechanism inside `WriteRange` changes.
The temp-name uniqueness rule and `__sstmp_` collision guard from Plan 3b carry over.

## UI: drag in the preset list with a drop indicator
- `PresetListView`'s `ListBox` becomes drag-reorderable (Avalonia 11/12 `DragDrop`): press-drag a
  `PresetItem`, and an **insertion line** renders between the two items where it would drop (an
  adorner/separator bound to the computed target index), so the user sees which two presets the
  dragged one will sit between.
- On drop: compute `from` = dragged item index, `to` = insertion index; call
  `PresetListViewModel.MoveAsync(from, to)` → `ReorderService.MoveAsync` behind the existing
  `IsBusy`/`BusyMessage`/progress UI. Empty trailing slots are valid drop targets (drop "at the end").
- The up/down buttons remain for single-step nudges.
- Dragging an empty slot is disallowed (matches `ReorderService` guard).

## Components / files
```
src/Sonulab.Core/Services/ReorderService.cs        (rewrite WriteRange -> select+save rotation; keep API + verify/rollback)
src/Sonulab.App/ViewModels/PresetListViewModel.cs  (add MoveAsync(from,to) command for drop; keep MoveUp/Down)
src/Sonulab.App/Views/PresetListView.axaml(.cs)    (DragDrop + insertion-line drop indicator)
src/Sonulab.App/Behaviors/ReorderDropIndicator.cs  (attached behavior/adorner for the insertion line)
tests/Sonulab.Core.Tests/ReorderServiceTests.cs    (extend: select+save rotation correctness, temp-slot, no-empty fallback, rollback)
```

## Testing
- **Core (offline, FakePresetDevice):** the rewritten `MoveAsync` produces the correct final order
  AND correct content per slot (the fake models select=load-live, save-by-name=write-slot); moving
  with/without an available temp slot; rollback restores original on injected save failure;
  names stay unique through the rotation. The existing Plan 3b reorder tests are updated to the new
  mechanism (same assertions on order/content).
- **App (offline):** `PresetListViewModel.MoveAsync(from,to)` calls the service and reloads;
  drop-index→(from,to) mapping unit-tested; gated when writes disallowed.
- **Hardware (guarded, operator):** `--reorder-test` (already exists) re-run to confirm the fast
  engine on-device + time it; the drag UI verified by running the app.

## Risks / notes
- **Full device (30/30):** no temp slot → documented slow fallback (param-replay of the displaced
  preset) or a "free a slot" prompt. Rare; presets cap at 30 and users typically have empties.
- Selecting a preset changes the pedal's **active** preset (audible) during reorder — acceptable for
  a management action; the list ends on a benign selection.
- Save-by-name uniqueness: temp names (`__sstmp_<slot>`, short) + the reserved-prefix guard from 3b.
