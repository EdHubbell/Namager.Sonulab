# Design: Highlight & protect amp/IR files used by presets

**Date:** 2026-07-23
**Branch:** `feat-preset-usage-guard` (off `fix-connect-reconnect`)

## Problem

Amp files (`.vxamp`) and IR files (`.irblob`) on the pedal can be referenced by
presets. Today the amp/IR list tabs give no indication of whether a file is in
use, and deleting or renaming a file that a preset references silently breaks
that preset's reference (leaving a dangling name the device can no longer
resolve). We want to:

1. **Highlight** amp/IR files that are used by at least one preset.
2. **Deny delete** of a used file, with a message listing the presets.
3. **Deny rename** of a used file (same rationale — a rename dangles the link).

## Core insight

A preset stores its amp/IR selection as a **name string**, not a slot index.
Inside the preset document (the ASCII blob split into `path:{json}` node lines),
the relevant lines carry a schema `ref`:

```
root\app\amp\amp:{"desc":"Amp model","value":"Lead","type":"plist","ref":"root\\amp"}
```

- `ref == "root\amp"` → the node's `value` is the **amp name** the preset uses.
- `ref == "root\ir"`  → the node's `value` is the **IR name** the preset uses.

So "is this file used?" reduces to "does any preset document contain a node
whose `ref` is `root\amp`/`root\ir` and whose `value` matches this file's name?"

Names are unique per list (enforced on upload), which is what makes name-based
referencing reliable. Matching is **exact** (trailing whitespace trimmed only) —
this mirrors how the device itself resolves the reference.

## Architecture

Three layers, mirroring the existing Core (pure/tested) → App (I/O + caching) →
View (Avalonia) split.

### 1. `PresetUsageMap` — pure core logic (`src/Sonulab.Core/Services/`)

A pure, fully unit-testable value object built from already-loaded preset
documents. No device I/O.

```csharp
public sealed class PresetUsageMap
{
    // ampName (case-sensitive, trimmed) -> preset display names that use it
    // irName  -> preset display names that use it
    public static PresetUsageMap Build(
        IEnumerable<(string PresetName, PresetDocument Doc)> occupiedPresets);

    // Returns the (sorted, distinct) preset names using this amp/IR, or empty.
    public IReadOnlyList<string> PresetsUsingAmp(string ampName);
    public IReadOnlyList<string> PresetsUsingIr(string irName);
}
```

**Extraction rule:** iterate each document's node records; for every record
whose schema `ref` equals `root\amp`, record `(value → presetName)` in the amp
map; likewise `root\ir` → ir map. This is generic over how many IR nodes a
preset has (e.g. cab + reverb IR) and needs no hard-coded node paths. Empty
`value`s are skipped. Empty/unoccupied presets are never passed in.

**Node access:** `PresetDocument` exposes its lines; each parses to a
`NodeRecord`/`NodeSchema` from which `Ref` and `value` are available (see
`NodeSchema.Ref`, `PresetDocument.GetValueJson`). If a convenient enumerator
over records with schema doesn't already exist, add a small
`IEnumerable<NodeRecord> Records()` (or equivalent) to `PresetDocument` — a
focused, self-contained addition.

### 2. `PresetUsageService` — app layer caching (`src/Namager.App/Services/`)

Owns device I/O and the cache. Constructed once in `MainWindowViewModel` and
shared (by reference) into `PresetListViewModel`, `AmpListViewModel`, and
`IrListViewModel`.

```csharp
public interface IPresetUsageService
{
    // Builds the map on first call (lazy) by reading every occupied preset
    // document off the device, then caches it. Subsequent calls return the
    // cached map until Invalidate() is called.
    Task<PresetUsageMap> GetAsync();

    // Marks the cache dirty; next GetAsync() rebuilds. Called after any
    // preset mutation.
    void Invalidate();
}
```

- **Lazy build:** the map is built the **first time the Amps or IR tab is
  opened** (whichever comes first), not at connect. Reading all occupied preset
  documents over USB serial takes a few seconds, so `GetAsync()` runs under a
  status-bar operation scope ("Checking preset usage…") the first time.
- **Shared across both tabs:** one map serves both Amps and IR.
- **Caching:** result cached until invalidated. Tab switches after the first
  build are instant.
- **Invalidation triggers:** `PresetListViewModel` calls `Invalidate()` after
  any preset mutation — write, reorder, delete, duplicate, rename.
- **File changes do NOT rescan presets.** Deleting/renaming/uploading an amp or
  IR does not change any preset's stored reference, so the map stays valid. The
  amp/IR list simply re-matches its (possibly changed) file names against the
  cached map in memory — cheap, no device reads. (Uploading a file whose name
  matches a previously-dangling reference will correctly show as used on the
  next in-memory match.)

Reads presets via the existing `DeviceRepository` (`ListPresetsAsync` for the
occupied slots + names, `ReadPresetAsync` for each document).

### 3. Item view models + views

`AmpItemViewModel` and `IrItemViewModel` each gain:

- `bool IsUsed`
- `IReadOnlyList<string> UsedInPresets`
- `string? UsedInTooltip` — e.g. `"Used in: Clean, Lead, Rhythm"` (null when unused)

These are populated when the list is (re)loaded, by looking each item's `Name`
up in the cached `PresetUsageMap`.

**Highlight (View):** used rows get a **subtle accent treatment** — a tinted row
background or a left accent bar — driven by an existing Sonulab theme token
(e.g. `Sonulab.AccentBrush` / an appropriate token), styled for **both light and
dark** variants. No hex literals in `.axaml` (per project convention). Exact
token/treatment chosen during implementation to read clearly but not shout.

**Tooltip (View):** `ToolTip.Tip` bound to `UsedInTooltip`, shown only when
`IsUsed`.

### 4. Delete & rename guards

Both live in the list VMs, checked **before** any device write.

- **Delete** (`AmpListViewModel.DeleteAsync`, `IrListViewModel.DeleteAsync`):
  before the existing `RunAsync(... DeleteAmpAsync/DeleteIrAsync ...)`, look up
  the selected item in the cached map. If used, **block** (no write) and surface
  the message; otherwise proceed as today.

- **Rename** (`AmpListViewModel.CommitRenameAsync`,
  `IrListViewModel.CommitRenameAsync`): at the guard seam (after the blank/no-op
  checks, before `RunAsync(... RenameAmpAsync/RenameIrAsync ...)`), if the item
  is used, **block** — set `IsEditing = false` (matching the existing
  failure/gated path) and surface the message; otherwise proceed as today.

Both guards need the map. Since guards must be synchronous-ish at the click,
they use the already-cached map (the tab that shows these commands has, by
definition, already triggered the lazy build on open). If for any reason the map
isn't built yet, the guard awaits `GetAsync()` first.

**Message surfacing:** reuse the existing inline `ErrorMessage` seam (already
paired with `_status.Failure(...)` in each VM), rendered with **text wrapping**
so a multi-line list shows fully:

> This IR file is used in the following presets: Clean, Lead, Rhythm. You can
> only delete files that aren't in an active preset.

(Amp variant: "This amp file is used…".) Plus a short single-line status-bar
failure for the always-visible channel, e.g. `Can't delete 'X' — used by 3
presets`. No new modal dialog (the app has no message-box service today; adding
one is unwarranted for a blocking notice).

## Data flow

```
Connect ──> presets loaded (names only, eager, unchanged)
                │
Open Amps/IR tab ──> EnsureTabLoaded ──> list RefreshAsync
                                            │
                                            ├─ read amp/IR file names (as today)
                                            └─ await PresetUsageService.GetAsync()
                                                   │ (first time: read all occupied
                                                   │  preset docs under status scope,
                                                   │  build PresetUsageMap, cache)
                                                   ▼
                                          set IsUsed/UsedInPresets/UsedInTooltip
                                          per item  ──> View highlights + tooltips

Delete/Rename click ──> guard checks cached map
                          ├─ used  ──> block + inline message + status.Failure
                          └─ free  ──> existing RunAsync write path

Any preset mutation (Preset tab) ──> PresetUsageService.Invalidate()
                                        (next Amps/IR open rebuilds)
```

## Testing

**Core (`tests/Sonulab.Core.Tests`)** — `PresetUsageMap`:
- Amp/IR name → correct set of preset names.
- Multiple IR nodes in one preset are all captured.
- Empty `value` nodes skipped.
- Two presets using the same amp both listed; results distinct & sorted.
- Name matching is exact (trimmed); a non-matching name is absent.
- A document with no amp/IR ref nodes contributes nothing.

**App (`tests/Namager.App.Tests`)** — via existing `FakePresetDevice`:
- After list load, a used item has `IsUsed == true`, correct `UsedInPresets` /
  tooltip; an unused item `IsUsed == false`.
- `DeleteAsync` on a used item: no device delete occurs; `ErrorMessage` contains
  the preset list; status set to failure.
- `DeleteAsync` on an unused item: proceeds (delete happens).
- `CommitRenameAsync` on a used item: no device rename; message set; edit mode
  exited.
- `CommitRenameAsync` on an unused item: proceeds.
- `PresetUsageService`: caches (second `GetAsync` doesn't re-read); `Invalidate`
  forces a rebuild.

## Out of scope (YAGNI)

- Cascade-rename that rewrites preset references to follow a renamed file.
- Highlighting presets themselves (feature is about amp/IR files).
- IR-slot / amp-slot metadata changes.
- A general-purpose modal dialog / message-box service.

## Conventions honored

- Core stays UI-free and unit-tested; app layer handles I/O + caching.
- Theme tokens only in `.axaml` (no hex); light + dark variants.
- Device writes remain gated by the existing `RunAsync`/`CanMutate` path; guards
  only *prevent* writes, never introduce new unguarded ones.
