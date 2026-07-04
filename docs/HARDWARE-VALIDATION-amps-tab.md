# Hardware validation — Amps tab (sub-project 2b)

Manual checks requiring the pedal (VoidX-Control CLOSED; app via `dotnet run --project src/Sonulab.App`
from the repo root so `docs/backups/` and `NAMFiles/Distilled/` resolve). Run top to bottom; every
write here is guarded (auto-backup to `docs/backups/`), but read the result line before proceeding.

## Checks

- [ ] **List**: connect; Amps tab shows all 30 slots; names match `dotnet run --project tools/HwCheck -- --list-amps`.
- [ ] **Upload from .nam (the Definition of Done)**: pick a known `.nam` from `NAMFiles/FullCaptures/`,
      watch Distilling → Writing chunk n/98 → Verifying → Done; new amp appears in the list;
      select it ON THE PEDAL and play — must sound like the source NAM.
- [ ] **C# vs Python distiller ear check (deferred from Phase 1)**: upload the SAME `.nam` distilled by
      Python (`python tools/distiller/distill.py <nam> out.vxamp`, upload out.vxamp via the tab's
      .vxamp path) into a second slot; A/B by ear — must be indistinguishable.
- [ ] **Upload from .vxamp**: pick a `docs/backups/amp-*.vxamp` or `NAMFiles/Distilled/*.vxamp`;
      uploads without distilling; sounds right.
- [ ] **Rename** (protocol untested on amps until now — `chunk:-1` name write): rename an amp from the
      tab; confirm the new name on the pedal AND that the amp still sounds unchanged (content intact).
- [ ] **Delete**: delete a test amp; slot shows (empty); pedal no longer lists it; backup file landed
      in `docs/backups/amp-<slot>-*-deleted.vxamp`.
- [ ] **No empty slots** path: with all 30 slots full (or temporarily), Upload shows the blocked
      message and opens no panel.
- [ ] **Cancel during distill**: start a `.nam` upload, hit Cancel while "Distilling…" — clean
      "Cancelled." error, nothing written to the device (amp list unchanged).
- [ ] **VoidX interop**: open VoidX-Control afterwards; it must list the uploaded amp(s) normally.

## Recovery

Any slot damaged during testing: restore with
`dotnet run --project tools/HwCheck -- --upload-amp docs/backups/<file>.vxamp <slot> --name "<name>"`.
