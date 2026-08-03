# Project status — shipped, pending, and ranked follow-ups

Moved out of `CLAUDE.md` (2026-07-25) so it loads on demand rather than in every session.

## Not done

**Preset, amp, and IR slot reorder are all SHIPPED** via the atomic `dswap` verb + shared
`SlotBubbleReorder` engine (presets = Cycle 1, amp/IR = Cycle 2, 2026-07-24; per-row up/down buttons on
every tab, no usage rescan). Backup-all UI is still deferred from the v1 tabs. Manual UI checks
pending in `docs/HARDWARE-VALIDATION-amps-tab.md` and `docs/HARDWARE-VALIDATION-irs-tab.md`
(`docs/HARDWARE-VALIDATION-plan-dragreorder.md` was the earlier preset-reorder checklist). Performance pass done — before/after numbers in `docs/perf-findings.md`;
the preset-dwrite question is resolved (VERDICT in PROTOCOL.md; byte-exact restore/duplicate via dwrite is a possible follow-up, not built).
Ranked follow-ups (dswap reorder + targeted usage-map done Cycles 1–2; remaining: byte-exact dwrite
restore, riding review minors): `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md`. Paced serial pipelining is BUILT
(multi-chunk foreground `dread` overlaps sends at a 30 ms floor with lockstep repair; kill switch
`SerialLinkOptions.PipelineEnabled`) — on-device checks pending in
`docs/HARDWARE-VALIDATION-pipelining.md`. The background usage scan is deliberately not pipelined.
- Preset-usage warm start: reconnect seeds highlights from `%APPDATA%\Namager\preset-usage-cache.json`
  (keyed by pedal id, per-slot name match); the background scan still runs to completion and corrects.
  Pipelining the scan (groups of 4, ~14 s → ~8 s) remains open and composes with this.
- Restore Snapshot SHIPPED (exact-mirror, byte-exact staged writes incl. presets, skip-if-identical
  resume, safety backup checkbox); Import Snapshot removed. Live restore validated informally
  2026-08-03 (worked as expected); the itemized probes in `docs/HARDWARE-VALIDATION-restore.md`
  (notably the active-slot-write probe) remain for a methodical pass.

Amp metadata hardware validation (docs/HARDWARE-VALIDATION-amp-metadata.md) pending — run before relying on SSMD blocks on-device. IR-slot metadata not designed.
UI-polish visual checklist (docs/HARDWARE-VALIDATION-ui-polish.md) pending.
Tone3000 live checklist (docs/HARDWARE-VALIDATION-tone3000.md) pending.
Disconnect handling is SHIPPED (typed `DeviceDisconnectedException`, `SonuClient` latch, app dead
state, HwCheck exit 2); on-device checks pending in `docs/HARDWARE-VALIDATION-disconnect.md`.
