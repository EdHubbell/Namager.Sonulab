# Hardware validation — Restore Snapshot

Restore writes byte-exact slot content via the staged dwrite sequence. Presets use this path
IN-APP FOR THE FIRST TIME here (HwCheck --preset-dwrite-probe proved the sequence on 2026-07-04;
PROTOCOL.md VERDICT). Run top to bottom; stop on any failure.

- [ ] Baseline: Export Snapshot of the current pedal (keep this file — it is the day's backup).
- [ ] Single-preset probe first: restore a snapshot onto the SAME pedal unchanged — every slot
      should report "already identical" (skip), zero writes. Proves the compare path end to end.
- [ ] Change ONE preset's amp selection on the pedal, re-run the same restore: exactly 1 file
      written (that preset), everything else skipped; preset sounds/looks correct afterward.
- [ ] ACTIVE-SLOT probe: make the pedal's live preset one that the restore will overwrite;
      re-run a restore that writes it. Watch for audio glitches, wrong live state, or a wedged
      device. If the pedal misbehaves, STOP — mitigation (select another preset before writing
      the active slot) is a known follow-up; note findings here.
- [ ] Full mirror restore onto this pedal from a snapshot with deliberate differences
      (one renamed preset, one deleted amp, one extra IR): writes+clears match the confirm
      dialog's counts; pedal content matches the snapshot afterward (spot-check via VoidX or
      HwCheck --list-amps / --list-irs / no-arg preset list).
- [ ] Cancel mid-restore (during the amp stage): dialog says canceled-between-files; re-run
      resumes and finishes with the early slots skipped.
- [ ] Safety backup: confirm pre-restore-<timestamp>.namsnap lands in Documents\NAMager Backups
      and re-restoring FROM it returns the pedal to its pre-restore state.
- [ ] Cross-pedal (if second unit available): restore pedal A's snapshot onto pedal B; firmware
      mismatch note appears if applicable; pedal B matches A's content.
- [ ] Timing note: record full-restore wall time here for the docs: ______ min for __ files.
