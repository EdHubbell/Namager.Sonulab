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

Preset Level SHIPPED: `root\app\output\pst\level` is now the top block of the parameter editor
(slider + explanation), with "match volume to another preset" backed by an offline K-weighted
loudness estimate (`Sonulab.Distill.Loudness` / `LevelModel`). Hardware checks pending in
`docs/HARDWARE-VALIDATION-preset-level.md`. Two deliberate gaps:
- The spec's `AmpLoudnessCache` was **not built** — `LevelModel` needs the amp BLOB, not a
  scalar (the model is nonlinear), so a cached loudness cannot short-circuit the read. Amp blobs
  are memoized per session instead. If bulk normalize later wants persistence, the right shape
  is a preset-keyed estimate cache: `(deviceId, slot, presetName, hash of level-relevant values)`
  → `RelativeLufs`.
- Bulk "normalize the whole bank" is deliberately NOT built — see the Deferred section of
  `docs/superpowers/specs/2026-08-03-preset-level-design.md`; when it is, the apply path should
  be byte-exact dwrite, not select+save.
The `amp\vol` %→dB taper in `LevelModel.AmpVolGainDb` is an ASSUMPTION (50 % treated as unity)
and is the first thing to calibrate against the device VU meters.

Modulation block SHIPPED (2026-08-04): `root\app\mod` is now editable in the parameter editor,
positioned between Impulse Response and Delay. The parameter editor's nesting is now recursive,
enabling the Modulation block's sub-folders (Tone and Character, Tremolo) and the Tremolo block's
nested Rate sub-folder. Hardware validation checklist in `docs/HARDWARE-VALIDATION-modulation.md`.
