# Hardware validation — amp slot SSMD metadata

**Feature:** SSMD metadata block in the amp-slot padding region (spec:
`docs/superpowers/specs/2026-07-06-amp-metadata-design.md`).

**Open assumption to settle:** firmware ignores slot bytes [8256, 12288) rather than
validating/checksumming them. The corpus only ever contained zeros there.

Prereqs: VoidX-Control CLOSED; pedal on COM (auto-discovered); at least one EMPTY amp slot.

## Checklist

- [ ] 1. `dotnet run --project src/Sonulab.App` — connect to the pedal.
- [ ] 2. Amps tab → Upload .nam… → pick any known-good `.nam` from `NAMFiles/`.
       Fill Notes ("hw validation test") and Link (any URL). Upload to an empty slot.
       Expect: upload completes, read-back verify passes (it compares all 12288 B,
       so the SSMD block already survived one flash round-trip).
- [ ] 3. Select the new amp in the list. Expect: details pane shows source file, size,
       date, sha256-backed fields, fit error, notes, and the link.
- [ ] 4. **Power-cycle the pedal** (unplug USB + power, replug). Reconnect the app.
- [ ] 5. On the PEDAL, select the test amp in a preset and PLAY through it.
       Expect: loads normally, sounds like the source model, no glitching/reboot.
- [ ] 6. In the app, select the test amp again. Expect: metadata intact after power cycle.
- [ ] 7. Edit notes ("edited after power cycle") → Save to pedal. Expect: ~3 s guarded
       write succeeds; details pane shows the new note; amp still plays fine.
- [ ] 8. Rename the test amp on the pedal (F2 in the app). Re-select. Expect: metadata
       survives a rename (rename is a chunk:-1 name write; blob untouched).
- [ ] 9. Delete the test amp slot. Confirm `docs/backups/` got the pre-delete backup and
       `VxampMetadata.TryRead` on that backup file returns the block (spot-check via
       a REPL or by re-uploading the backup and viewing details).
- [ ] 10. Record results here (date, firmware version, pass/fail per step).

**If step 5 fails** (amp won't load / device misbehaves): the firmware does inspect the
padding. Delete the slot immediately, mark the feature blocked, and fall back to the
local-sidecar alternative from the spec's rejected-alternatives section.
