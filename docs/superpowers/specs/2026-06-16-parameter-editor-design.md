# Parameter Editor — Design Spec (Feature A)

Status: approved in brainstorming 2026-06-16. Builds on the merged `Sonulab.App` ViewModel layer
(`ParameterEditorViewModel`, `ParameterFieldViewModel`) and `Sonulab.Core`
(`SonuClient.BrowseRecordsAsync`, `NodeRecord`, `NodeSchema`).

## Goal
Make the loaded parameters legible: group them by effect block with headings, expose them via a
forward-compatible blocklist, and label them from a swappable translation file. Blocks in scope:
**gate, exp, comp, amp, eq, ir, delay, reverb** (Output skipped for now).

## Problem with today's editor
`ParameterEditorViewModel` browses `root\app` and produces a flat list of fields with no block
context — you can't tell which `on_off` belongs to which effect. The block is encoded in the node
path (`root\app\<block>\…`), and the device returns a `desc` per node, but container nodes
(e.g. `root\app\delay\tcfolder`) need friendlier names than `desc` gives.

## Components

### 1. `LabelService` (Sonulab.App)
- Loads `labels.en.json` (embedded resource + optional on-disk override): a flat map of
  **node path → display text**, e.g. `{ "root\\app\\delay\\tcfolder": "Tone and Character", "root\\app\\delay": "Delay" }`.
- `string Label(string path, string? deviceDesc)`: returns the JSON mapping if present; else the
  device's `desc` if non-empty; else the last path segment prettified (e.g. `lo_cut` → "Lo Cut").
- Language is swappable by loading a different `labels.<lang>.json`; default English.

### 2. `ParameterExposure` (Sonulab.App) — the blocklist
- Loads `hidden-params.json`: a list of node paths and/or path prefixes to HIDE
  (e.g. `["root\\app\\output", "root\\app\\*\\_st"]`). Default-show everything else.
- `bool IsHidden(string path)`: exact path or prefix/glob match.
- Rationale: new firmware params appear automatically; only known-noise nodes are suppressed.

### 3. `PresetEditorViewModel` (replaces/extends `ParameterEditorViewModel`)
- `LoadCommand`: `BrowseRecordsAsync("root\app")` → group records into the 8 in-scope blocks by the
  second path segment (`root\app\<block>\…`). For each block, build a `BlockSectionViewModel`.
- Each `BlockSectionViewModel` has: `Header` (LabelService for `root\app\<block>`), `IsExpanded`,
  and an ordered list of items. Items are either:
  - a **`ParameterFieldViewModel`** (an editable leaf: float/enum/plist not hidden), or
  - a **`SubGroupViewModel`** (a folder node like `tcfolder`/`ir2`/`wfolder`): a labeled sub-header
    plus its own editable leaves, nested one level.
- A field/subgroup is included iff `!ParameterExposure.IsHidden(path)` and it's an editable leaf
  (or a folder containing editable leaves). `ParameterFieldViewModel.Label` uses `LabelService`.
- `SaveCommand`: unchanged behavior — write only dirty fields, then
  `write root\app\preset:{"value":<name>,"save":"save"}` (the dirty-tracking from the merged VM stays).

### 4. View — `ParameterEditorView.axaml`
- One scrollable pane of `Expander`s, one per `BlockSectionViewModel` (header = block label).
- Inside: rows per field (Label + the existing Kind-based control: slider/combo/textbox), and a
  sub-header + indented rows per `SubGroupViewModel`.
- Built-in `Expander` (Avalonia 12 FluentTheme); no third-party deps.

## Data flow
connect → `PresetEditorViewModel.Load` → `browse root\app` → group by block → apply blocklist +
labels → render Expander sections. Edit a field → dirty. Save → write dirty leaves + save preset.

## Files
```
src/Sonulab.App/Services/LabelService.cs
src/Sonulab.App/Services/ParameterExposure.cs
src/Sonulab.App/labels.en.json            (embedded; English map — user-authored)
src/Sonulab.App/hidden-params.json        (embedded; blocklist)
src/Sonulab.App/ViewModels/PresetEditorViewModel.cs   (was ParameterEditorViewModel)
src/Sonulab.App/ViewModels/BlockSectionViewModel.cs
src/Sonulab.App/ViewModels/SubGroupViewModel.cs
src/Sonulab.App/Views/ParameterEditorView.axaml(.cs)  (Expander sections)
tests/Sonulab.App.Tests/LabelServiceTests.cs
tests/Sonulab.App.Tests/ParameterExposureTests.cs
tests/Sonulab.App.Tests/PresetEditorViewModelTests.cs (grouping/blocklist/labels via FakeSonuLink.SeedBrowse)
```

## Testing
- `LabelService`: JSON hit, `desc` fallback, prettified-segment fallback.
- `ParameterExposure`: exact + prefix/glob hide; default-show.
- `PresetEditorViewModel`: `browse root\app` (seeded on `FakeSonuLink`) groups into the right blocks,
  hides blocklisted nodes, surfaces a new (unmapped, non-hidden) param automatically, nests a folder
  as a sub-group, save writes only dirty leaves. All offline against the fakes.

## Out of scope
Output block; per-language files beyond English (format supports them); knob/skeuomorphic styling.
