# Hardware validation — IRs tab

Gates 1–2 (blob format, chunk:-1 commit probe) already ran during implementation — see
docs/ir-format.md and the ir-tab ledger. Remaining ear/eye checks (VoidX-Control CLOSED,
app via `dotnet run --project src/Sonulab.App` from the repo root):

- [ ] **List**: IRs tab matches `dotnet run --project tools/HwCheck -- --list-irs`.
- [ ] **Upload from .wav (the DoD)**: pick a `NAMFiles/IR/*.wav`, watch Writing chunk n/34 →
      Verifying → Done; select the IR on the pedal and PLAY — must sound like the same cab as
      the VoidX-uploaded copy of that wav (A/B them).
- [ ] **Upload from .irblob**: restore a `docs/backups/ir-*.irblob` or `NAMFiles/IrDump/*.irblob`.
- [ ] **Rename / Delete**: from the tab; pedal reflects both; delete leaves a backup in docs/backups.
- [ ] **VoidX interop**: open VoidX-Control afterwards; it lists/uses the uploaded IR normally.

Recovery: `dotnet run --project tools/HwCheck -- --upload-ir <backup.irblob> <slot> --name "<name>"`.
