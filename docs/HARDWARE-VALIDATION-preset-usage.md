# Hardware / Visual Validation — Preset-usage highlighting and guards

Run with the pedal connected (USB, VoidX-Control CLOSED). Check each item.

**Design note (2026-07-24):** the usage scan is now a **background, progressive** scanner
(`PresetUsageService`) — it no longer blocks the Amps/IR tab on open. The list renders immediately
from the (fast) name list; highlights fill in preset-by-preset as the background scan resolves each
slot's head (≤32 chunks/preset, windowed read). Delete/rename guards call `EnsureCompleteAsync()`,
which finishes the scan **urgently** (foreground lane) before deciding — so a guard may show
"Checking preset usage…" briefly even after the list has been visible for a while. This checklist
supersedes the older BLOCKING-scan version; items below reflect the new UX.

## Checklist

- [ ] 1. **Immediate list, progressive highlights.** Connect, then open the Amps tab for the first
  time. The amp list appears within ~2 s — it must NOT wait on "Checking preset usage…" before
  showing rows. Watch the list over the next ~15–30 s: highlights (amber accent, SemiBold) appear
  on used amps one at a time as the background scan resolves each preset, without any visible
  stutter or re-render of the whole list. Repeat on the IR tab.

- [ ] 2. **Highlight correctness.** Once the scan settles (no more highlights appearing, ~15–30 s
  after tab open), cross-check by hand against the Presets tab: every amp/IR actually referenced by
  an occupied preset shows the amber/SemiBold highlight; amps/IRs referenced by no preset show
  normal text style. Hover a used amp/IR — the tooltip reads "Used in: <preset names>" and lists
  exactly the presets that reference it (compare against the preset list). Hover an unused amp/IR —
  no "Used in" tooltip.

- [ ] 3. **Delete guard timing (used vs. unused, scan in flight).** Immediately after connecting
  (before the background scan has settled), select an amp known to be referenced by a preset and
  invoke Delete. Expect: the button/flow shows a brief "Checking preset usage…" state (the guard is
  urgently finishing the scan), then blocks with the wrapped error message naming the referencing
  preset(s) — no amp is deleted. Now select an amp known to be UNUSED and invoke Delete: it proceeds
  normally (after the backup prompt) with no "Checking preset usage…" stall and no error message.
  Repeat both halves on the IR tab.

- [ ] 4. **Rename guard.** Select a used amp, rename it (F2 / context menu) — blocked, wrapped error
  message names the blocking preset(s), row unchanged. Select an unused amp and rename it —
  succeeds, no error. Repeat both on the IR tab.

- [ ] 5. **Invalidate + rescan after a preset edit.** Go to the Presets tab, edit a preset so it now
  references a *different* amp (e.g. change its amp parameter and save-from-live, or duplicate/edit
  a slot). Revisit the Amps tab: the highlight should move off the old amp and, after a short
  rescan, land on the new amp. Confirm the old amp no longer shows "Used in" for that preset and the
  new amp does.

- [ ] 6. **Scan vs. user device ops (no corruption).** Reconnect so the background scan is actively
  filling highlights, then immediately start an amp upload (Amps tab → upload a `.vxamp`) while
  highlights are still resolving. The upload must proceed normally — no interleaved/corrupted
  writes, no stall waiting on the scan. After it completes, verify the uploaded amp shows correct
  content (the upload path's own byte-check should pass) and that the scan resumes/continues
  filling in the remaining highlights afterward.

- [ ] 7. **WiFi smoke.** Unplug USB so the app falls back to WiFi (or otherwise force the WiFi
  path), reconnect, and repeat check 1: list appears immediately, highlights fill in progressively
  over WiFi too. (WiFi read latency is higher than serial — allow more time, but the list must still
  not block on the scan.)

- [ ] 8. **Deletion of a used preset clears its highlights.** On the Presets tab, delete a preset
  that uniquely used a given amp/IR (after backup confirmation). Return to the Amps/IR tabs — after
  the rescan, that amp/IR is no longer highlighted (now unused), assuming no other preset also
  references it.

- [ ] 9. **Theme check.** Check both light and dark themes (Windows Settings > Personalization >
  Colors). The amber accent highlight and danger-red error/guard message are readable and
  intentional in both variants.
