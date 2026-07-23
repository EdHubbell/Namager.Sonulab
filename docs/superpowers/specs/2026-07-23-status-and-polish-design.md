# Status &amp; Polish Release — Design

**Date:** 2026-07-23
**Addresses:** GitHub issues #4 (progress on connect), #5 (tooltips + layout polish), #6 (status bar + write feedback)
**Type:** UX feedback + polish (no protocol/transport changes)

## Problem

The app rarely tells the user what it is doing or whether an action worked.

- **No global status/progress feedback.** Connecting shows no progress at all — the user can't tell whether the app is probing USB, falling back to WiFi, or reading data (#4). There is no way to tell when a preset copy has finished (#6).
- **Success is essentially invisible.** A preset Save only makes the "dirty dot" vanish; there is no explicit confirmation. This led the user to be unsure whether preset changes actually saved to the pedal (#6). *(This release surfaces write results clearly; it does NOT hunt for an actual silent-save bug — if one is observed it is filed separately.)*
- **Feedback is duplicated and scattered.** Each of the three list ViewModels (Presets/Amps/IRs) re-implements the same `IsBusy` + `BusyMessage` + indeterminate `ProgressBar` triple, and errors live in 5+ independent inline properties.
- **Layout / interaction rough edges (#5):** long Tone3000 model names are truncated next to "Send to pedal" with no tooltip; the Amps and IR list panes are different widths from the Presets list; the amp detail panel sits too high / too far left; and after an amp upload the inline upload panel stays open instead of revealing the new amp's detail card.

## Goals

1. One consistent, always-visible signal for "what's happening" and "did it work."
2. Progress + staged status text during connect and long operations.
3. Explicit success/failure feedback for every device write.
4. The concrete layout/interaction fixes from #5.

## Non-goals (out of scope)

- Investigating whether device writes can *actually* silently fail (feedback-only decision; file separately if reproduced).
- Amp/IR reorder UI, backup-all UI.
- Modal error dialogs — errors stay non-modal (status bar + contextual inline).

## Architecture

### `IStatusService` (single shared channel)

A DI-injected singleton that is the app's one channel for busy/progress/success/error. It replaces the three duplicated per-list busy triples. It lives in `src/Namager.App/Services/` (UI-facing, no protocol logic — same tier as `LabelService`/`ParameterExposure`), **not** in `Sonulab.Core`.

- **API:**
  - `IOperationScope BeginOperation(string message, bool determinate = false)` — pushes a busy state; returns a scope.
    - `IOperationScope.Report(double progress)` — update determinate progress (0..1).
    - `IOperationScope.Report(string message)` — update the message mid-operation (e.g. staged connect text).
    - `IOperationScope.Dispose()` — pops the operation; reverts the bar to idle (or to the next operation on the stack).
  - `void Success(string message)` — transient success terminal state (auto-reverts to idle after a delay).
  - `void Failure(string message)` — persistent error terminal state (stays until the next operation begins or the user dismisses it).
- **State model:** the service exposes one observable `StatusState`:
  - `Kind` ∈ `{ Idle, Busy, Success, Error }`
  - `Message : string`
  - `Progress : double?` (null ⇒ indeterminate/none)
  - `IsIndeterminate : bool`
- **Nesting:** operations are stack-based; the top-of-stack operation drives the bar. `Success`/`Failure` set a terminal state that a subsequent `BeginOperation` overrides.
- **Timing:** success auto-revert uses an injected clock/delay abstraction so it is unit-testable (no real `Task.Delay` in tests).
- **Threading:** state changes marshal to the UI thread (the service is consumed by VMs already on the UI dispatcher; guard for background callers).

### What changes in the VMs/views

- `PresetListViewModel`, `AmpListViewModel`, `IrListViewModel`: replace their `IsBusy`/`BusyMessage` fields and `RunAsync`/`RefreshAsync` busy bookkeeping with `using var op = _status.BeginOperation(...)` and `op.Report(...)`. Route terminal results through `_status.Success/Failure`.
- Remove the per-list indeterminate `ProgressBar`s from `PresetListView.axaml`, `AmpListView.axaml`, `IrListView.axaml`.
- **Kept inline (contextual, field-anchored):** the parameter editor's red error next to Save, and the amp upload panel's own error/blocked lines. Their *busy/success* signal also flows to the status service; their error text stays inline **and** mirrors to the bar on failure.

## Feature detail

### 1. Status bar (UI)

A persistent bar docked at the bottom of `MainWindow` (below the `SplitView`), full width, bound to `StatusState`:

- **Left — status text:**
  - Idle: connection summary (e.g. "AMP Station — 30 presets") or "Ready" when disconnected.
  - Busy: the current operation message.
  - Success: "✓ &lt;message&gt;" for ~4s, then revert to idle.
  - Error: "⚠ &lt;message&gt;" (Danger token); persists until the next operation or click-to-dismiss.
- **Right — progress bar:** determinate when real progress is known (upload chunk n/m); indeterminate for unknown-length operations; hidden when idle.
- **Styling:** uses `SonulabTheme` tokens (`TextMutedBrush`, `WarningBrush`, `DangerBrush`, `AccentBrush`) for both theme variants; no hex literals in the view.

### 2. Connect experience (#4)

- The Connect button shows a busy/hourglass state (spinner glyph + disabled) while connecting.
- The connect + initial-load sequence reports staged status to the bar via `IStatusService`:
  `"Probing USB…"` → `"Connecting over WiFi…"` (only if USB fails) → `"Reading presets…"` → `"Reading amps…"` → `"Reading IRs…"` → idle summary.
- `ConnectionViewModel` / session-load emits each stage through the status service rather than only setting a final `Status` string. The existing connection dot + summary text remain.

### 3. Write feedback (#6)

- Every device write routes a terminal result to the bar:
  - Preset Save → "✓ Saved" / "⚠ Save failed: …"
  - Preset copy / reorder → "✓ Moved '…'" (and the operation shows progress while running, so the user can see when a copy completes)
  - Delete / duplicate / rename → matching "✓ …" / "⚠ …"
- **Preset Save specifically:** on success show an explicit transient "✓ Saved" (today success is only the dirty-dot vanishing). On failure, keep the inline red text next to Save **and** show the persistent bar error. No modal dialogs.

### 4. Polish fixes (#5)

1. **Tone3000 model-name tooltip:** add `ToolTip.Tip` bound to the full model name on the truncated `TextBlock` in `Tone3000View.axaml` (next to "Send to pedal"), so a truncated name is recoverable on hover.
2. **Consistent list widths:** standardize the Amps and IRs list panes to the **same 360px width and grid-column idiom as the Presets list**, retiring the IR view's different `MaxWidth=560` layout.
3. **Amp detail positioning:** fix the amp detail panel extending too high / too far left — align its top and left margins to match the presets editor's alignment.
4. **Amp upload auto-close:** on successful upload, set `IsUploadPanelOpen = false` after the reload/select so the newly-selected amp's detail card is revealed automatically; the "✓ Uploaded '…' to slot N" confirmation goes to the status bar.

## Testing

- **`StatusService` unit tests:** state transitions (Idle→Busy→Success→Idle; Idle→Busy→Error persists), operation stacking (nested begin/dispose), success auto-revert via injected clock, error persistence until next op / dismiss.
- **VM tests** against a fake status service:
  - Preset Save success reports "✓ Saved"; failure reports an error and keeps fields dirty.
  - Amp upload success closes the upload panel and selects the new slot.
  - Connect emits the staged messages in order.
- **Manual / visual checklist** (added to `docs/HARDWARE-VALIDATION-status-and-polish.md`): status bar behavior on real device ops, connect staging, tooltip hover, list-width consistency, amp detail alignment, upload auto-close.

  Tests live under `tests/Namager.App.Tests/` (`StatusService` is an app-tier service, so no `Sonulab.Core` test changes are expected).

## Key files to touch

- `src/Namager.App/Services/` — new `StatusService` + `StatusState`; wire into `PresetListViewModel`, `AmpListViewModel`, `IrListViewModel`, `ConnectionViewModel`, `ParameterEditorViewModel`, `MainWindowViewModel`.
- `src/Namager.App/Views/MainWindow.axaml` — add bottom status bar; connect-button busy state.
- `src/Namager.App/Views/PresetListView.axaml`, `AmpListView.axaml`, `IrListView.axaml` — remove per-list indeterminate bars; standardize list-pane widths.
- `src/Namager.App/Views/AmpDetailPanel.axaml` (+ `AmpListViewModel.cs` upload flow) — detail alignment; upload auto-close.
- `src/Namager.App/Views/Tone3000View.axaml` — model-name tooltip.
- `src/Namager.App/Styles/SonulabTheme.axaml` — status-bar style class if needed (tokens only).
- Tests under `tests/Namager.App.Tests/`.
