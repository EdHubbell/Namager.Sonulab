# Hardware validation — preset-usage warm-start cache

Feature: reconnect shows amp/IR usage highlights instantly from
`%APPDATA%\Namager\preset-usage-cache.json`; the background scan revalidates and corrects.
All checks are read-only for the pedal except where marked.

- [ ] Cold connect (cache file deleted): Amps/IRs tabs behave exactly as before — highlights
      fill progressively over the scan (~15–30 s), amp detail shows "Checking…" then results.
- [ ] Disconnect, restart NAMager, reconnect: highlights on the Amps and IRs tabs appear
      **< 1 s** after connect; amp detail shows entries with the "verifying…" badge; badge
      clears when the scan completes; highlights unchanged (cache agreed with the device).
- [ ] Stale in-place edit: with NAMager closed, change one preset's amp on the pedal
      (front panel) or in VoidX [DEVICE WRITE — needs the pedal owner]. Reconnect: the OLD
      highlight shows provisionally, then corrects itself when the scan reaches that slot;
      the cache file afterwards contains the new amp name.
- [ ] Rename/delete outside the app: with NAMager closed, rename or delete a preset in VoidX
      [DEVICE WRITE]. Reconnect: that slot contributes NO provisional highlight (name
      mismatch drops it); it reappears (or stays gone) once the scan covers it.
- [ ] Guard unchanged: during the provisional phase (badge visible), attempt an amp delete →
      the guard still blocks/waits on the real scan, not the cache.
- [ ] Second pedal (if available): connect pedal B → its map caches under its own id;
      reconnect pedal A → A's warm start unaffected.
