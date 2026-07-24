# Spec — Tab layout alignment (Presets / Amps / IRs)

**Date:** 2026-07-24
**Status:** Design approved (Ed, 2026-07-24). Ready for writing-plans.
**Sequencing:** SPEC ONLY for now. Another agent is working in the same view files on this branch
(`feat-amp-ir-reorder`). No code from this spec may be written until that work is merged.

## Goal

Switching between the Presets, Amps and IRs tabs should not move the UI. Today Presets and IRs are
pixel-identical to each other, but Amps shifts: its toolbar and list sit 8px further left and 6px
higher, and its list is 16px wider. Two related defects surface alongside it — the Amps toolbar
overflows its 360px column so the Delete button spills into the detail area, and the Presets detail
pane's Load/Save buttons are not in the same horizontal band as the list toolbar.

After this change all three tabs share one page shape, enforced by shared style classes rather than
by repeated literals, so the tabs cannot drift apart again.

## Scope & non-goals

- **In scope:** toolbar-row and list geometry on all three list tabs; right-pane (detail) top-edge
  alignment; removal of the header Move Up/Down buttons and the ViewModel commands behind them;
  shared layout tokens + style classes in the theme; the message-row margin inconsistency.
- **Non-goals:** the Tone3000 tab; drag-and-drop; the internal layout of the parameter editor or
  the amp detail card below their top rows; unifying the three near-identical ListBox
  `ItemTemplate`s (a worthwhile later refactor, deliberately not bundled here); any device/protocol
  behavior.

## Background verified against the code (2026-07-24)

All three pages are hosted in `MainWindow.axaml` containers with `Margin="12"`
(`MainWindow.axaml:119`, `:131`, `:137`). Inside that:

| | Presets | IRs | Amps |
|---|---|---|---|
| Root element | `DockPanel` in the 360 column | `DockPanel MaxWidth="360" HorizontalAlignment="Left"` (`IrListView.axaml:22`) | `Grid ColumnDefinitions="360,*"` (`AmpListView.axaml:22`) |
| Toolbar margin | `8,6,8,4` (`PresetListView.axaml:20`) | `8,6,8,4` (`IrListView.axaml:25`) | **`0,0,0,6`** (`AmpListView.axaml:26`) |
| List margin | `8,0` (`:39`) | `8,0` (`IrListView.axaml:89`) | **none** (`AmpListView.axaml:52`) |
| List content width | 344 (360 − 16) | 344 (360 − 16) | **360** |
| Message row margin | n/a | `8,0` (`:47`, `:50`) | **`0,0,0,4`** (`:47`) |
| Right pane | editor, `Margin="12,0,0,0"` (`MainWindow.axaml:125`) | none | detail, **`Margin="16,34,0,0"`** (`:105`) |

Additional findings:

- The `34` in the Amps detail margin is a hand-tuned magic number standing in for "below the
  toolbar". It will drift the next time the toolbar's button content changes.
- The Presets detail pane has its **own** Load/Save row inside it
  (`ParameterEditorView.axaml:6`, `Margin="8"`), so its top inset is 8 against the list toolbar's 6.
  That row also contains a wrapping `ErrorMessage` TextBlock (`:11-13`).
- The Amps toolbar holds six buttons — Refresh, Move Up, Move Down, `Upload .nam…`,
  `Upload .vxamp…`, Delete — measuring roughly 396px against a 360px column, which is why Delete
  extends into the detail area (observed by Ed, 2026-07-24). IRs has the same six-button shape and
  therefore overflows too; it has no detail pane, so the overflow is not visible there.
- The identical `Button.reorder` style block is duplicated in all three views
  (`PresetListView.axaml:9-15`, `AmpListView.axaml:14-20`, `IrListView.axaml:14-20`).
- Header Move Up/Down are bound to selection-based commands that are **distinct** from the per-row
  ones: `MoveUpAsync`/`MoveDownAsync` at `PresetListViewModel.cs:87,99`,
  `AmpListViewModel.cs:167,177`, `IrListViewModel.cs:158,168`; the per-row `MoveItemUpAsync`/
  `MoveItemDownAsync` live at `PresetListViewModel.cs:111,121`, `AmpListViewModel.cs:187,195`,
  `IrListViewModel.cs:178,186`.
- Ten call sites drive the selection-based commands from tests:
  `PresetListViewModelTests.cs:34,98,259,270,271,341`, `AmpListViewModelTests.cs:879,893`,
  `IrListViewModelTests.cs:323,336`. One is commented `// toolbar path`.
- `Namager.App.Tests` has **no** `Avalonia.Headless` reference — the App tests are ViewModel-only.
  An automated pixel assertion would require taking a new test dependency; this spec does not.

## The page shape

Every list tab renders as:

```
page container  (MainWindow, Margin 12)
├─ left pane    [toolbar row: Height = ToolbarHeight, Margin = PageInset]
│               [list:        Margin = ListInset]
└─ right pane   [toolbar row: Height = ToolbarHeight, Margin = PageInset]
                [detail content]
```

Because both panes' toolbar rows are pinned to the same height with the same top inset, the list's
top edge and the detail pane's top edge land on the same y — the alignment rule Ed specified. IRs
has no right pane, so it renders the left column only.

Where a pane has no buttons (the Amps detail pane), it gets a `ToolbarHeight`-tall spacer in place
of the row. This replaces the magic `34` with a value derived from the same token that sizes every
other toolbar.

## Layout tokens

Added to `src/Namager.App/Styles/SonulabTheme.axaml`, alongside the existing color tokens. This
extends the convention CLAUDE.md already mandates for color ("use tokens, never hex literals in
views") to spacing.

| Token | Type | Value | Provenance |
|---|---|---|---|
| `Sonulab.PageInset` | `Thickness` | `8,6,8,4` | today's Presets/IRs toolbar margin |
| `Sonulab.ListInset` | `Thickness` | `8,0` | today's Presets/IRs list margin |
| `Sonulab.ToolbarHeight` | `Double` | `32` | see below |
| `Sonulab.PaneGap` | `Thickness` | `12,0,0,0` | today's Presets editor gap (`MainWindow.axaml:125`) |

`PaneGap` is declared as a `Thickness`, not a `Double`, because it is consumed as a `Margin` — XAML
cannot widen a `Double` resource into a `Thickness` without a converter. `ToolbarHeight` stays a
`Double` because it is consumed as `Height`.

`32` is Fluent's default `Button.MinHeight`, which both the icon buttons (16px icon + 5/6 padding =
27, floored to 32) and the text buttons are expected to already hit. **The first implementation
task measures the rendered height of today's Presets toolbar and pins the token to the measured
value**, so the Presets tab does not shift. If it measures other than 32, the measured number wins
and this table is corrected.

## Style classes

Also in `SonulabTheme.axaml`:

- **`.slot-toolbar`** — `Margin` = `PageInset`, `Height` = `ToolbarHeight` (a hard pin, not
  `MinHeight`). Paired with a descendant rule `StackPanel.slot-toolbar > Button` setting
  `Height` = `ToolbarHeight` and `MinHeight` = `0`, so icon-only and text buttons are the same
  height **by construction** — a future button whose content would otherwise be taller cannot
  reintroduce the shift.
- **`.slot-list`** — `Margin` = `ListInset`.
- **`.slot-message`** — one margin for the inline warning/error `TextBlock`s. Today IRs uses `8,0`
  and Amps uses `0,0,0,4`, so when an error appears the list drops by a different amount on each
  tab. Same defect class, fixed in the same pass.

Each view replaces its literal margins with these classes. The numbers then exist in exactly one
place, which is the enforcement mechanism — divergence becomes impossible rather than merely
tested-against.

The duplicated `Button.reorder` style is hoisted out of the three views into the theme at the same
time. Zero visual change; same anti-duplication rationale.

## Header button sets

The Move Up / Move Down buttons are removed from all three headers. Every row already carries its
own up/down chevrons, so the header pair is redundant. Removing two icon buttons also brings the
Amps toolbar to roughly 312px and the IRs toolbar to roughly 298px, both comfortably inside the
360px column — **the overflow is resolved by the removal, with no change to any button label.**

| Tab | Header after |
|---|---|
| Presets | Refresh, Duplicate, Delete |
| Amps | Refresh, `Upload .nam…`, `Upload .vxamp…`, Delete |
| IRs | Refresh, `Upload .wav…`, `Upload .irblob…`, Delete |
| Presets detail | Load, Save, dirty-dot indicator |

If measurement at implementation time shows a toolbar still exceeding the column, the fallback is
an upload icon plus a short label (`↑ .nam`) rather than widening the column.

The per-row chevrons and their `CanMoveUp`/`CanMoveDown` gating are untouched.

## Code changes behind the removal

`MoveUpAsync`/`MoveDownAsync` are deleted from all three ViewModels, along with the `CanMutate` /
`CanRefresh` bindings that existed only on the removed buttons. The ten test call sites listed
above are rewritten onto the per-row `MoveItemUpCommand`/`MoveItemDownCommand`, passing the item
that the selection-based call implied. Test count is unchanged — the tests are rewritten, not
dropped — and the behaviors they cover (usage-map updates, error surfacing, empty-slot relocation)
are preserved on the path that still has a UI caller.

## The parameter editor's error row

Pinning `ParameterEditorView`'s toolbar row to `ToolbarHeight` would clip its wrapping
`ErrorMessage` TextBlock when an error runs to two lines. The TextBlock therefore moves out of the
toolbar row into its own `.slot-message` row directly below it — matching how Amps and IRs already
place their message rows. The dirty-dot indicator stays inline in the toolbar; it is a single
glyph.

## Files touched

| File | Change |
|---|---|
| `src/Namager.App/Styles/SonulabTheme.axaml` | 4 tokens, 3 style classes, hoisted `Button.reorder` |
| `src/Namager.App/Views/PresetListView.axaml` | drop 2 buttons + local style; apply classes |
| `src/Namager.App/Views/AmpListView.axaml` | drop 2 buttons + local style; apply classes; detail margin → `PaneGap` |
| `src/Namager.App/Views/IrListView.axaml` | drop 2 buttons + local style; apply classes incl. message rows |
| `src/Namager.App/Views/ParameterEditorView.axaml` | toolbar adopts `.slot-toolbar`; error moves to its own row |
| `src/Namager.App/Views/AmpDetailPanel.axaml` | `ToolbarHeight` spacer row; message rows use `.slot-message` |
| `src/Namager.App/Views/MainWindow.axaml` | editor margin → `PaneGap` |
| `src/Namager.App/ViewModels/{Preset,Amp,Ir}ListViewModel.cs` | delete `MoveUpAsync`/`MoveDownAsync` |
| `tests/Namager.App.Tests/{Preset,Amp,Ir}ListViewModelTests.cs` | rewrite 10 call sites onto the per-row commands |
| `docs/HARDWARE-VALIDATION-ui-polish.md` | add the tab-cycle alignment check |

## Verification

1. `dotnet build` clean.
2. `dotnet test` — all green, count unchanged.
3. A lint-style test (no new dependency): parse the three list views' `.axaml` and assert the
   toolbar and list elements carry `.slot-toolbar` / `.slot-list` rather than literal `Margin`
   values, so a future edit cannot quietly re-hardcode a spacing number.
4. Manual, added to `docs/HARDWARE-VALIDATION-ui-polish.md`: cycle Presets → Amps → IRs → Presets
   and confirm (a) the first toolbar button does not move, (b) the list's left and top edges do not
   move, (c) on Presets and Amps the detail pane's top edge is level with the list's top edge, and
   (d) on Amps no toolbar button extends past the list's right edge.

Item 3 is a backstop, not the primary guarantee — the single-definition tokens are what actually
prevent drift.

## Risks

- **File collision.** Every view file here is in play for the other agent on this branch.
  Implementation must wait for that merge; starting early guarantees conflicts.
- **`ToolbarHeight` measurement.** If today's Presets toolbar does not render at 32, pinning to 32
  would shift the tab Ed is happy with. Mitigated by measuring first and letting the measurement set
  the token.
- **Hard `Height` pin.** Content taller than `ToolbarHeight` will clip rather than grow the row.
  That is the intended trade — it is what makes the band stable — but it is why the editor's
  wrapping error text is moved out of the row.
- **Overflow margin is ~48px on Amps.** Comfortable but not enormous; a future long-labelled button
  could re-overflow. The short-label fallback is recorded above.

## Key references

- `CLAUDE.md` — "UI colors come from Styles/SonulabTheme.axaml tokens … never hardcode hex in
  .axaml"; this spec extends that rule to spacing.
- `docs/superpowers/specs/2026-07-06-ui-polish-design.md` — the theme tokens this builds on.
- `docs/superpowers/specs/2026-06-16-per-row-reorder-buttons-design.md` — the per-row chevrons that
  make the header pair redundant.
- `docs/HARDWARE-VALIDATION-ui-polish.md` — where the manual check lands.
