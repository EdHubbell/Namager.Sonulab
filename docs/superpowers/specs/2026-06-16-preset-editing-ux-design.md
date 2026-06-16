# Preset-editing UX improvements — design

**Date:** 2026-06-16
**Status:** Approved, ready for planning

## Problem

Four UX rough edges in the preset workflow:

1. **Block rows resize.** In the parameter detail view each effect block is an
   `Expander`; collapsed, it shrinks to its header-text width instead of staying
   the panel width like it does when expanded — the column "jumps".
2. **No enable cue.** You can't tell at a glance whether an effect block is on
   without expanding it to read its "Enable" combobox.
3. **Manual load.** Selecting a preset in the list does nothing to the detail
   view; you must press "Load". The two panels feel disconnected.
4. **Renaming is awkward.** Rename happens via an unintuitive text field + button
   at the bottom of the preset panel, not in the list where the name lives.

## Scope

All four changes live in `src/Sonulab.App` (ViewModels + Views). **No `Sonulab.Core`
changes.** Out of scope (flagged, not done): the device's `mod` (Modulation) block
is missing from the editor's `Blocks_InScope` and isn't shown at all — a separate
pre-existing gap.

## Confirmed device facts (from a live `browse root\app`, fw 2.5.1)

- A block's enable toggle is a child leaf **`on_off`**, `type:"enum"`,
  `options:["ON","OFF"]`, `desc:"Enable"`. Present on every toggleable block and
  subgroup (`gate\on_off`, `comp\on_off`, `amp\on_off`, `ir\on_off`, `delay\on_off`,
  `reverb\on_off`, `exp\on_off`, …). **`eq` has no `on_off`** (no enable).
- Because `on_off` is an `enum`, it is already rendered as an editable "Enable"
  combobox field inside each block (it passes the editor's `EditableTypes` filter).
- Editor blocks in scope: `gate, exp, comp, amp, eq, ir, delay, reverb`.

## Feature A — Block rows: constant width

**View only** (`ParameterEditorView.axaml`). The `Expander` per block does not
stretch horizontally when collapsed. Fix:

- `Expander`: `HorizontalAlignment="Stretch"` and `HorizontalContentAlignment="Stretch"`.
- `ScrollViewer`: `HorizontalScrollBarVisibility="Disabled"` so content wraps to the
  viewport width rather than letting a block widen the virtual canvas.

Every block then fills the panel width whether expanded or collapsed. No VM change.
Verified by build + manual eyeball.

## Feature B — Enable indicator per block

**`BlockSectionViewModel`** gains:

- A reference to its enable field: `public ParameterFieldViewModel? EnableField { get; set; }`
  (the block's `on_off` field, or null).
- A derived `public bool? Enabled` →
  `EnableField is null ? (bool?)null : string.Equals(EnableField.Text, "ON", OrdinalIgnoreCase)`.
- In the constructor (or a setter), subscribe to `EnableField.PropertyChanged` and
  raise `OnPropertyChanged(nameof(Enabled))` so the header updates live when the user
  flips the combobox.

**`ParameterEditorViewModel.LoadAsync`**: after a block's fields are built, set
`section.EnableField = section.Fields.FirstOrDefault(f => f.Path.EndsWith("\\on_off", Ordinal))`.

**`ParameterEditorView.axaml`**: replace the `Expander.Header` string binding with a
small horizontal panel — a `PathIcon` (`Icon.Power`, already in `Icons.axaml`) + the
title `TextBlock`. The icon:
- `IsVisible="{Binding Enabled, Converter=NotNull}"` (hidden when `Enabled` is null, e.g. `eq`).
- Foreground/opacity reflects on/off: accent + full opacity when `Enabled` is true,
  dimmed when false. Implemented with a converter (`BoolToBrush`-style) or two bound
  properties. Display-only — the existing combobox remains the control.

(A nullable-bool → visibility/brush converter is added under `Converters/`.)

## Feature C — Auto-load selected preset (activate + load)

**`ParameterEditorViewModel`**:

- `[ObservableProperty] private bool _isLoading;`
- `private string? _loadedName;`
- New command/method `LoadForAsync(string presetName)`:
  - If `presetName == _loadedName`, return (dedup — prevents reload churn when a
    reorder/rename re-sets the list selection to the same preset).
  - `IsLoading = true;` in a `try/finally`:
    - Activate: `await _client.WriteAsync(@"root\app\preset", "\"" + presetName + "\"")`
      (this changes the device's live/playing preset — confirmed acceptable).
    - `await LoadAsync()` (browse + rebuild blocks).
    - `PresetName = presetName; _loadedName = presetName;`
  - `finally { IsLoading = false; }`

**`ParameterEditorView.axaml`**: bind the root content `IsEnabled="{Binding !IsLoading}"`
and overlay a subtle "Loading…" indicator (a centered `ProgressBar`/text) visible
while `IsLoading`.

**Wiring — `MainWindowViewModel`**: in the `Connected` handler, after `Presets` and
`Editor` are created, subscribe to `Presets.PropertyChanged`; when `e.PropertyName ==
nameof(PresetListViewModel.Selected)` and `Presets.Selected` is a non-empty item, call
`Editor.LoadForCommand.Execute(Presets.Selected.Name)`. Empty slot → skip. The thin
subscription is the only glue; the testable logic lives in `LoadForAsync`.

The editor's existing manual **Load** button stays (re-reads the active preset).

## Feature D — In-place rename + context menu + F2 (remove bottom bar)

**`PresetItemViewModel`**:
- `[ObservableProperty] private bool _isEditing;`
- `[ObservableProperty] private string _editName = "";`

**`PresetListViewModel`**:
- `BeginRenameCommand(PresetItemViewModel? item)`: if `item` non-empty and not
  `IsBusy` → `item.EditName = item.Name; item.IsEditing = true;`
- `CommitRenameCommand(PresetItemViewModel? item)`: if `item` is editing → let
  `name = item.EditName.Trim();` if `name.Length > 0 && name != item.Name` →
  `await RunAsync($"Renaming…", () => _repo.RenameAsync(item.Index, name))` (disables
  the list and reloads, which recreates items and clears edit state); otherwise set
  `item.IsEditing = false` (cancel/no-op).
- `CancelRenameCommand(PresetItemViewModel? item)`: `item.IsEditing = false;`
- The old `RenameAsync(string?)` command (driven by the bottom bar) is **removed**.

Notes:
- `CommitRename` must guard on `item.IsEditing` so the **Escape-then-LostFocus**
  sequence (Escape sets `IsEditing=false`, then the vanishing TextBox raises
  `LostFocus`) does not re-commit the abandoned edit.
- After a successful rename the list reloads and selection clears (consistent with
  other list mutations like delete/duplicate); this does **not** trigger an editor
  reload (the selection→load subscription ignores a null `Selected`). Leaving the
  reloaded selection cleared avoids redundant re-activation of the just-renamed
  preset.

**`PresetListView.axaml`**:
- In the row template, the name area becomes a `TextBlock` (`IsVisible="{Binding !IsEditing}"`)
  plus a `TextBox` (`IsVisible="{Binding IsEditing}"`, `Text="{Binding EditName}"`,
  `MaxLength="31"` for the device name cap). The `TextBox` has `KeyBindings`:
  `Enter` → `CommitRenameCommand`, `Escape` → `CancelRenameCommand` (both with
  `CommandParameter="{Binding}"`, reaching the list VM via `$parent[ListBox]`); and a
  `LostFocus` handler that commits.
- A `ContextMenu` on the row (`DockPanel.ContextMenu`) with a single `MenuItem`
  "Rename" → `BeginRenameCommand` (`CommandParameter="{Binding}"`).
- A `ListBox`-level `KeyBinding`: `F2` → `BeginRenameCommand` with
  `CommandParameter="{Binding Selected}"`.
- **Focus:** when the edit `TextBox` becomes visible it must take focus and select
  all text. Add a small attached behavior `Behaviors/EditBoxBehavior.cs` exposing
  `EditBoxBehavior.FocusOnVisible` (bool attached property): when set true on a
  `TextBox`, hook `IsVisible`/`AttachedToVisualTree` so that on becoming visible it
  calls `Focus()` + `SelectAll()`. Bind it to `IsEditing`.
- **Remove** the bottom rename `Grid` (`RenameBox` + Rename button).

## Data flow

- **Select → load:** user clicks a preset → `PresetListViewModel.Selected` changes →
  `MainWindowViewModel` subscription fires `Editor.LoadForCommand(name)` →
  `IsLoading=true` (detail view disabled + "Loading…") → activate + browse → blocks
  rebuilt with enable indicators → `IsLoading=false` (detail view enabled).
- **Rename:** F2 / right-click → `BeginRename` (row shows TextBox, focused, all
  selected) → Enter → `CommitRename` → `RunAsync` renames on device + reloads list →
  fresh items (edit state cleared).

## Error handling

- `LoadForAsync` wraps activate+load in `try/finally` so `IsLoading` always clears,
  even if the device read fails (detail view re-enables rather than sticking disabled).
- `CommitRename` goes through the existing `RunAsync`, which is gated by
  `writesAllowed` and always clears `IsBusy` in its `finally`; an empty/unchanged name
  is a silent no-op. Device name cap enforced by `MaxLength=31`.

## Testing

**App VM unit tests** (`FakeSonuLink` already supports `SeedBrowse`/`SeedScalar`;
seed `on_off` enum leaves per the real schema):

- **B:** `LoadAsync` sets `BlockSectionViewModel.Enabled` true for a seeded
  `amp\on_off="ON"`, false for `gate\on_off="OFF"`, and null for a block with no
  `on_off` (`eq`). Toggling the `on_off` field's `Text` flips `Enabled` (live update).
- **C:** `LoadForAsync("X")` issues the activate write (`root\app\preset` = "X"),
  builds blocks, sets `PresetName`/loaded name, toggles `IsLoading` true→false, and is
  a no-op when called again with the same name (dedup).
- **D:** `BeginRename` sets `IsEditing`/`EditName`; `CommitRename` with a changed name
  renames via the repo and reloads; empty or unchanged name is a no-op; gated when
  `writesAllowed:false`.

**XAML / behaviors** (build-verify + manual eyeball): A width fix, the header icon,
the in-place edit template, context menu, F2 binding, and the focus behavior.

## Components touched

- `ViewModels/BlockSectionViewModel.cs` — `EnableField`, `Enabled` (B).
- `ViewModels/ParameterEditorViewModel.cs` — `EnableField` wiring (B); `LoadForAsync`,
  `IsLoading`, `_loadedName` (C).
- `ViewModels/PresetItemViewModel.cs` — `IsEditing`, `EditName` (D).
- `ViewModels/PresetListViewModel.cs` — rename commands; remove old `RenameAsync` (D).
- `ViewModels/MainWindowViewModel.cs` — selection→load subscription (C).
- `Views/ParameterEditorView.axaml` — width (A), header icon (B), loading overlay (C).
- `Views/PresetListView.axaml` (+ `.axaml.cs` for LostFocus) — in-place rename,
  context menu, F2, remove bottom bar (D).
- `Behaviors/EditBoxBehavior.cs` — focus-on-visible attached property (D).
- `Converters/` — nullable-bool → visibility/brush for the enable icon (B).
