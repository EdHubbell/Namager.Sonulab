# Per-row reorder buttons — design

**Date:** 2026-06-16
**Status:** Approved, ready for planning

## Problem

Reordering presets today is done two ways, both unsatisfying:

- **Drag-to-reorder** is slow and a little buggy.
- The **top command-bar Move up / Move down buttons** operate on the current
  selection and are an awkward way to nudge an entry.

We want a direct, obvious control: an **up and a down button on the right-hand
side of each non-empty preset row**. Clicking one moves that preset by exactly
one physical slot. While the move runs, the list is disabled until it refreshes.
The move must use the most efficient device operation possible.

## Scope

- **Add** per-row up/down buttons to the preset list.
- **Keep** the existing drag-to-reorder and top Move up/down buttons unchanged.
- **Optimize** the single-step "move into an empty neighbor" case in the reorder
  engine so it uses a cheap 1-copy relocate instead of the slow param-replay.

Out of scope: removing drag, changing the top buttons, multi-step moves, or any
amp/IR work.

## Behavior decisions (confirmed with user)

1. **Old controls:** keep drag and the top Move up/down buttons; just add the
   per-row buttons alongside.
2. **Empty neighbors:** up/down always moves the preset by exactly one physical
   slot. If the adjacent slot is empty, the preset relocates into it, leaving a
   gap (no skipping over empties to find the next preset).
3. **Boundaries:** buttons always render for non-empty rows; the unavailable one
   (Up on the first row, Down on the last row) is shown **disabled/greyed**, not
   hidden.
4. **Empty rows:** show no reorder buttons at all.

## Efficiency rationale

`PROTOCOL.md` confirms the device has **no native reorder/swap primitive**;
reordering must physically copy preset content between slots via `select`+`save`
(~216 ms per copy) or, as a fallback, param-replay (~12 s per slot).

For a single-step move there are two cases:

- **Adjacent slot occupied (a swap):** the existing fast path
  (`RotateViaSelectSaveAsync`) already does this in **3 copies (~0.65 s)**, which
  is optimal for a swap. We reuse it.
- **Adjacent slot empty (a relocate):** today this hits
  `WriteRangeViaReplayAsync` (~12 s) because the affected range contains an empty
  slot. But it is really just one relocate. We add a fast path that does it in
  **1 copy (~0.2 s)**.

This keeps the per-row buttons fast in every case, and the list is only disabled
for a fraction of a second.

## Components

### 1. `Sonulab.Core` — `ReorderService`

Add a focused single-step method:

```csharp
public async Task MoveStepAsync(int from, bool up,
    IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
```

- Compute `to = up ? from - 1 : from + 1`. Validate `from`/`to` are in range and
  that slot `from` is non-empty; if `to` is out of range, it is a no-op.
- Read the current slots once.
- **If slot `to` is empty → fast relocate** (new private helper):
  1. `select(origName[from])` to load the preset into the live buffer.
  2. Rename the empty slot `to` to a unique temp name (reuse `TempPrefix`).
  3. `SaveCurrentAsAsync(tempName)` so the device copies the live content into
     slot `to`.
  4. `Delete(from)`.
  5. Rename slot `to` to the preset's original name.
  - Back up slot `from` (and `to`, which is empty) first; on any failure, delete
    the temp/partial and restore from backup using the existing
    `RestoreRangeAsync`-style rollback. Read-back verify the relocated slot.
- **If slot `to` is occupied → delegate to existing `MoveAsync(from, to)`** (the
  3-copy adjacent swap; replay fallback only if the device is full with no temp
  slot available anywhere).
- Reuse the existing temp-name guard (refuse to run if any live preset name
  already uses `TempPrefix`).

The existing public `MoveAsync` is unchanged, so drag and the top buttons keep
working exactly as before.

### 2. `Sonulab.App` — `PresetItemViewModel`

- Add `bool CanMoveUp` and `bool CanMoveDown`, computed when the list is built
  (the builder knows each slot's index and the total count):
  - `CanMoveUp = !IsEmpty && Index > 0`
  - `CanMoveDown = !IsEmpty && Index < Total - 1`
- Empty rows get both `false`.
- Items are recreated on every reload, so these are set once at construction —
  no change notification churn needed.

### 3. `Sonulab.App` — `PresetListViewModel`

- Add `MoveItemUpCommand` / `MoveItemDownCommand`, each taking the row's
  `PresetItemViewModel`.
- Each runs through the existing `RunAsync(message, work)`:
  `work = () => _reorder.MoveStepAsync(item.Index, up: true/false)`.
  `RunAsync` already sets `IsBusy`, runs the move, reloads the list, and clears
  `IsBusy` — giving the "disabled until refreshed" behavior for free.
- Guard inside the command against empty items and out-of-range steps (defensive;
  the UI already disables those buttons).

### 4. `Sonulab.App` — `PresetListView.axaml`

- In the row `DataTemplate`, switch the layout to a `DockPanel`/`Grid` so two
  small flat icon buttons can dock to the **right** of the row, with the
  slot number + name filling the rest.
- Buttons reuse `Icon.ChevronUp` / `Icon.ChevronDown` (already in `Icons.axaml`).
- `IsVisible` bound so buttons only appear on non-empty rows.
- `IsEnabled` bound to `CanMoveUp` / `CanMoveDown` so the unavailable button is
  greyed at the boundaries.
- Buttons reach the parent VM via compiled binding to the view's `DataContext`
  (e.g. `{Binding #PresetList.((vm:PresetListViewModel)DataContext).MoveItemUpCommand}`
  or the equivalent ancestor binding), with `CommandParameter="{Binding}"`.
- Add `IsEnabled="{Binding !IsBusy}"` to the `ListBox` so the whole list and its
  per-row buttons are disabled while a move runs and re-enable on reload.

## Data flow

1. User clicks a row's Up/Down button.
2. `MoveItemUp/DownCommand` fires with the row item → `RunAsync` sets `IsBusy`
   (list disabled) → `ReorderService.MoveStepAsync(index, up)`.
3. `MoveStepAsync` picks relocate (empty neighbor, 1 copy) or swap (occupied
   neighbor, existing 3-copy fast path) and applies it with verify + rollback.
4. `RunAsync` reloads the list from the device and clears `IsBusy` → list
   re-enabled with the new order.

## Error handling

- `MoveStepAsync` inherits the service's existing guarantees: backup the affected
  slots, read-back verify after writes, and roll back to the backup on any
  failure (using `CancellationToken.None` for cleanup so a cancel still rolls
  back). A failed relocate deletes its temp/partial slot before restoring.
- `RunAsync` already wraps work in `try/finally` to always clear `IsBusy`, so a
  thrown error re-enables the list rather than leaving it stuck disabled.

## Testing

**Core (`Sonulab.Core.Tests`, against `FakePresetDevice`):**
- Move up/down with an **occupied** neighbor → presets swap; order correct.
- Move up/down into an **empty** neighbor → preset relocates, old slot becomes
  empty (gap preserved), name preserved.
- Assert the relocate path uses the **cheap copy** (e.g. via the fake's op log /
  select+save count) and does **not** trigger param-replay.
- Boundary: step up from index 0 / down from last index → no-op (or guarded).
- Failure injection mid-relocate → rollback restores original content + name and
  removes the temp slot.

**App (`Sonulab.App.Tests`):**
- `CanMoveUp` / `CanMoveDown` true/false at boundaries, false for empty rows.
- `MoveItemUp/DownCommand` pass the correct `index` and direction.
- List reloads after a move; `IsBusy` toggles around the operation.

**Build-verify + manual eyeball** the XAML row layout (buttons right-aligned,
greyed at boundaries, absent on empty rows), per project convention.
