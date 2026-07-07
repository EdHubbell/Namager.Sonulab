# UI polish ("Studio warm") + Amps master-detail — design

**Date:** 2026-07-06
**Status:** Approved in brainstorm (visual companion mockups in `.superpowers/brainstorm/14837-1783373193/content/`, gitignored — `final-look.html` is the design target; screenshot it before cleanup if needed).

## Problem

The app is visually stock — bare `<FluentTheme/>`, no accent, hardcoded hex literals scattered
through views. On the Amps tab, the metadata details and the upload panel both dock under the
list, crowding a column capped at 560 px while the right half of the window sits empty. The
Presets tab already uses a master-detail split; Amps should match it.

## Decisions (made in brainstorm)

- **Layout:** Amps tab becomes master-detail; the right panel is the single home for
  "the current amp" — details on selection, the upload form while uploading (Option B).
- **Style direction:** "Studio warm" — amber accent, warm dark / warm-paper light, ruled
  sections instead of nested cards inside panels, caps-spaced section labels, monospace slot
  numbers.
- **Theme:** keep `RequestedThemeVariant="Default"` (follow OS); every token defined for BOTH
  variants.
- **Scope:** cohesive polish across all three tabs + top bar. IRs keep their current
  single-column layout (skin only; master-detail when IR metadata exists someday).
- **Approach:** one shared theme layer + view-only restructure. **Zero ViewModel changes**;
  the 312-test suite must pass untouched.
- Constraints: Avalonia 12 built-in FluentTheme only (no FluentAvalonia, no packages, no
  custom control templates beyond styles/resources).

## 1. Theme layer — `src/Sonulab.App/Styles/SonulabTheme.axaml`

A single `Styles` file merged from `App.axaml` (styles + a `ResourceDictionary` with
`ThemeDictionaries`).

### 1.1 Palette tokens (ThemeVariant-scoped `Color` + `SolidColorBrush` resources)

| Token | Dark | Light |
|---|---|---|
| `Sonulab.AccentColor` | `#D9820F` | `#B96A08` |
| `Sonulab.AccentHoverColor` | `#E8A04A` | `#D9820F` |
| `Sonulab.SurfaceBrush` (panels/cards) | `#26221E` | `#FFFDF9` |
| `Sonulab.SurfaceAltBrush` (window/page bg, nav pane, top bar) | `#1E1B18` | `#F6F2EC` |
| `Sonulab.BorderBrush` (card borders, hairline rules) | `#3A332C` | `#DDD3C4` |
| `Sonulab.RuleBrush` (intra-card section rules; slightly softer than BorderBrush) | `#332D27` | `#EDE5D8` |
| `Sonulab.TextMutedBrush` | `#E8E2DA` at 55% | `#2B2620` at 55% |
| `Sonulab.SuccessBrush` (connected dot) | `#7BC47B` | `#4A9E4A` |
| `Sonulab.DangerBrush` (errors) | `#E06C68` | `#C0392B` |
| `Sonulab.WarningBrush` (budget/blocked messages) | `#E8A04A` | `#B96A08` |

Foreground text: rely on Fluent's variant defaults; only muted/accent text uses tokens.

### 1.2 Fluent accent overrides

Override `SystemAccentColor` plus `SystemAccentColorDark1/2/3` and
`SystemAccentColorLight1/2/3` with the amber ramp so stock controls (selection, focus,
progress bars, toggles, ComboBox highlights) adopt it app-wide. These overrides live in
`Application.Resources` (per-variant) so they are present before any control instantiates —
resource-ordering is a known risk; the plan must verify overrides visibly take effect in both
variants.

### 1.3 Style classes (selectors in the same file)

- `Border.card` — `Background=SurfaceBrush`, `BorderBrush=BorderBrush`, thickness 1,
  `CornerRadius=6`, `Padding=12`.
- `TextBlock.section-label` — ~10 px, `LetterSpacing`≈1 (via `TextBlock.LetterSpacing` if
  available in Avalonia 12; otherwise uppercase + FontSize alone), uppercase text convention,
  `Foreground=TextMutedBrush`.
- `TextBlock.slot-number` — `FontFamily="Consolas,Cascadia Mono,monospace"`,
  `Foreground=TextMutedBrush`.
- `Button.accent-outline` — transparent background, 1 px `Sonulab.AccentColor` border and
  foreground; hover = `Sonulab.AccentHoverColor` border/foreground. (Brush resources derived
  from the color tokens as needed — e.g. `Sonulab.AccentBrush`.)
- `Separator.rule` or `Border.rule` — 1 px `RuleBrush` horizontal rule used between card
  sections.
- `ListBoxItem` (global, within nav + slot lists): selected state gets a 3 px accent left
  bar (via `BorderThickness`/`BorderBrush` on the item container or a `Classes`-based
  template addition — implementer's choice, but NO control-template replacement) and an
  accent tint background (`AccentColor` at ~16% alpha).
- Window/page backgrounds: `SplitView` pane + top bar use `SurfaceAltBrush`; content area
  `SurfaceAltBrush` with cards on top.

### 1.4 Hex-literal migration

Existing inline colors migrate to tokens: `#D9534F` → `DangerBrush`, `#D9820F` (warning
texts) → `WarningBrush`, `#3B82D6` (details link button) → accent, `BoolToBrush.Connected`
converter's brushes → `SuccessBrush`/`TextMutedBrush` (converter may resolve resources or be
replaced by two styled Ellipse states — implementer's choice, no behavior change).

## 2. Amps master-detail restructure (view-only)

`AmpListView.axaml` becomes a `Grid ColumnDefinitions="260,*"`:

**Left column:** command bar (unchanged commands) + slot list. Slot number uses
`slot-number` class. The inline upload panel Border and the details Border are DELETED from
this column. `MaxWidth=560` on the old DockPanel is removed (the grid governs width).

**Right column:** new `src/Sonulab.App/Views/AmpDetailPanel.axaml` UserControl, inheriting
the page DataContext (`AmpListViewModel`). No new ViewModel, no code-behind logic beyond an
empty class. Three exclusive states, priority upload > details > placeholder:

1. **Upload form** — visible when `IsUploadPanelOpen`. Contains the complete existing upload
   UI (source filename header, name + slot row, Link/Notes fields, `NotesBudgetWarning`,
   progress bar, status, error, Start/Cancel/Close) inside a `card` Border, restyled with
   section labels. Bindings identical to today's.
2. **Details** — visible when `IsDetailsVisible && !IsUploadPanelOpen`. Card containing: amp
   name header (semibold, ~15 px) + `SLOT n` accent badge; ruled sections SOURCE (file,
   size, date), DISTILLED (version, fit error, uploaded date), NAM (passthrough fields),
   NOTES; the URL as an accent link button (`OpenDetailsUrlCommand`); `Edit notes/link`
   (`accent-outline`) and the existing edit sub-panel (EditNotes/EditUrl/`EditBudgetWarning`
   /Save/Cancel). Reading/no-metadata/`DetailsError` states as today, restyled.
   The `DetailsFields` ItemsControl presentation may be regrouped visually under the section
   rules, but the underlying `MetadataField` list binding stays as-is (labels already carry
   the grouping: Source file/Source size/…).
3. **Placeholder** — otherwise: centered `TextMutedBrush` hint "Select an amp to see its
   details." (Multi-state visibility via the existing booleans only — a `Panel` with three
   children and `IsVisible` bindings; upload-state binding wins by also gating the other two
   on `!IsUploadPanelOpen`.)

`ErrorMessage` and `UploadBlockedMessage` TextBlocks render at the top of the right column,
above whichever state is active.

**No ViewModel changes.** All bindings already exist on `AmpListViewModel`. All 312 tests
must pass without modification. (Follow-up noted from the amp-metadata final review: extract
a `SlotDetailsViewModel` only when IR metadata reuses this — explicitly out of scope now.)

## 3. Rollout to other views

Styling attributes only — no layout changes:

- **PresetListView / IrListView:** slot numbers get `slot-number`; their panels/inline
  editors get `card` where a bordered container already exists; hex literals → tokens.
- **ParameterEditorView:** section/group headers get `section-label`; container borders →
  `card`; no structural change.
- **MainWindow:** top bar Border → `SurfaceAltBrush` + bottom hairline; nav `ListBox` rows
  pick up the global selected-item treatment; Connect button → `accent-outline`; status dot
  → `SuccessBrush`/muted.
- **IrListView keeps single-column layout** (decision above).

## 4. Error handling / behavior invariants

- Pure view/style change: every command, gate (`CanMutate`, busy/uploading), and state
  transition behaves exactly as before. Any XAML binding referenced must already exist.
- Both theme variants must render intentionally: every token defined in BOTH
  `ThemeDictionaries`; missing-variant fallback to Fluent defaults is a bug.
- No new packages; no `ControlTemplate` replacements (style setters and resources only).

## 5. Verification

- `dotnet build` clean (XAML compiles; no AVLN warnings introduced beyond pre-existing).
- Full suite `dotnet test` — 312/312, zero test edits.
- Visual checklist (new `docs/HARDWARE-VALIDATION-ui-polish.md`, mostly device-free):
  both variants via Windows theme toggle; Amps tab states (placeholder → details → edit →
  upload form → done); Presets/IRs/editor skin sanity; connected/disconnected top bar; a
  side-by-side screenshot against the brainstorm mockup. Device needed only for the
  details/edit states (slot reads) — note which steps require the pedal.

## Out of scope

- IR tab master-detail; IR metadata.
- Custom title bar, app icon, branding beyond the palette.
- Custom control templates; FluentAvalonia or any package.
- ViewModel refactors (incl. `SlotDetailsViewModel` extraction).
