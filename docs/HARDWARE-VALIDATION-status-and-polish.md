# Hardware / Visual Validation — Status & Polish release

Run with the pedal connected (USB, VoidX-Control CLOSED). Check each item.

## Status bar (#4, #6)
- [ ] At rest before connecting, the bottom bar reads "Not connected".
- [ ] Clicking Connect: the mouse cursor becomes a wait/hourglass while connecting (the button stays a stable width — no in-button spinner); the bar shows "Connecting…", then "Reading presets…", then settles to the device summary (name + version + transport).
- [ ] Once connected, the Connect button is disabled (greyed) — clicking it again does nothing. It must NOT re-open the transport (that reset the pedal and wedged it). Reconnecting after a drop requires restarting the app.
- [ ] Visiting the Amps tab for the first time shows "Reading amps…" in the bar; the IRs tab shows "Reading IRs…".
- [ ] Saving a preset shows "✓ Saved" briefly (~4s), then the bar returns to the device summary.
- [ ] A preset move/duplicate/delete/rename shows "✓ Moved/Duplicated/Deleted/Renamed …".
- [ ] Force a failure (e.g. unplug mid-op): the bar shows a red "⚠ …" message that persists until the next operation. The app does NOT crash.
- [ ] During a preset copy/reorder, the bar's progress area is visibly active and clears when done — you can tell when the copy finishes.

## Layout polish (#5)
- [ ] The Amps list and IRs list are the same width as the Presets list (360px).
- [ ] The amp detail panel top-aligns with the amp list's first row (not floating above it) and is not pushed too far left.
- [ ] After a successful amp upload, the upload panel closes automatically and the new amp's detail card is shown and selected; the bar shows "✓ Uploaded '…' to slot N".
- [ ] After a FAILED amp upload, the panel stays open with the error visible.
- [ ] On the Tone3000 tab, hovering a truncated model name shows the full name in a tooltip.

## Theming
- [ ] The status bar, success (green) and error (red) colors read correctly in BOTH light and dark themes.
