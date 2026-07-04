# Editor Polish — ref-populated dropdowns + collapsed-by-default sections

**Date:** 2026-07-04
**Status:** Approved
**Context:** first of three follow-up sub-projects after 2b (this → IR tab → performance).
The revision list came from Ed's first real use of the merged Amps tab.

## Goal

Two parameter-editor fixes, no hardware required:
1. **Amp/IR picker dropdowns populate.** Params whose `browse` schema carries a `ref` to a device
   name-list node (`root\amp`, `root\ir`) render a ComboBox filled with that list's non-empty
   names. Today `NodeSchema.Ref` is parsed but never consumed, so those ComboBoxes are empty.
2. **Detail view starts collapsed and keeps its state.** All editor block sections default to
   collapsed; whatever the user expands stays expanded while switching presets, for the lifetime
   of the connection session (not persisted to disk — decided in brainstorming). (Blocks are the
   only expanders — subgroups render inside their block with no independent collapse.)

Out of scope (their own sub-projects, in order): the IR tab; the performance pass including the
guarded preset-content-dwrite re-test flagged in `PROTOCOL.md` ("OPEN QUESTION 2026-07-03").

## Design

### 1. Ref-populated dropdowns

- In `ParameterEditorViewModel.LoadAsync` (the browse+rebuild path that already runs on every
  preset switch): collect the distinct `schema.Ref` values that name a device list node, fetch
  each once via the existing `SonuClient.ReadListAsync(ref)`, and build an options list of the
  **non-empty** names.
- `ParameterFieldViewModel` gains the ref options at construction: when schema `Options` is empty
  and ref options exist, the field's `Options` become the ref list and its `Kind` renders through
  the EXISTING enum/plist ComboBox template — no XAML changes.
- **Current-value preservation:** if the field's current value is not in the fetched list (e.g.
  the amp was deleted), prepend it to the options so the ComboBox still displays it.
- **Freshness model:** options are re-fetched on every preset load. An amp/IR uploaded or renamed
  in another tab appears in the dropdown at the next preset switch (bounded staleness, no
  cross-tab coupling — Approach A from brainstorming; the cached/invalidation service was
  rejected as YAGNI).
- **Degradation:** if a ref-list read fails or returns nothing, options stay empty and the field
  renders exactly as today (string TextBox). The editor load never fails because of a ref fetch.
- Selection changes ride the editor's existing dirty-tracking + Save flow (`write` per dirty
  field, then save-preset); no special apply path.

### 2. Collapsed-by-default + per-session expansion state

- `BlockSectionViewModel.IsExpanded` default flips `true` → `false`. (Subgroups have no expander
  — `SubGroupViewModel` needs no change.)
- `ParameterEditorViewModel` keeps a `Dictionary<string, bool>` expansion map keyed by block
  header. It is written on every expander toggle (PropertyChanged subscription) and applied when
  sections are rebuilt on preset switch.
- Lifetime = the editor VM = the connection session. App restart (or reconnect) starts fully
  collapsed again — per-session only, per brainstorming decision.

## Testing

- `ParameterEditorViewModel` tests (existing FakePresetDevice harness; `SeedScalar(@"root\amp",
  "[\"names\"...]")` fakes the list read — no fake changes needed):
  - ref field gets ComboBox options = non-empty list names;
  - current value not in list → still present (prepended);
  - ref fetch returning nothing → field renders as string (no options, no crash);
  - sections default collapsed on first load;
  - expanding a block, then switching presets → block still expanded, untouched blocks collapsed.
- Full suite stays green (~226 tests today).

## Definition of done

Connect → open a preset: all sections collapsed. Expand Amp block: the amp picker is a dropdown
listing the device's amps; pick one, Save — it applies like any other param. Switch presets: the
Amp block is still expanded, everything else still collapsed, and the dropdown reflects the
device list. Full suite green.
