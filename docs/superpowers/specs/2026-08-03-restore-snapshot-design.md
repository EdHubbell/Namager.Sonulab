# Restore Snapshot — design

Date: 2026-08-03. Status: approved (Ed, this date, via remote session).

## Problem

A `.namsnap` export captures every occupied preset/amp/IR slot, but nothing can put those files
back on a pedal. The interim Import Snapshot feature (validate + learn IR identities, write
nothing) proved confusing — its user story was too thin to justify a menu item. Replace it with
the real thing.

## Decisions (made by Ed)

1. **All-or-nothing**: v1 restores the whole snapshot. No per-kind or per-slot selection.
2. **Exact mirror**: after restore, the pedal matches the snapshot byte-for-byte — slots occupied
   on the pedal but empty in the snapshot are CLEARED.
3. **Safety backup is a dialog checkbox**, default checked: "Back up current pedal state first
   (~3 min)" — a full `.namsnap` of the pedal's pre-restore state to `Documents\NAMager Backups`.
4. Also in scope on the same branch: **remove Import Snapshot** (menu, flow, `ImportSnapshotAsync`)
   and **add a pre-export explainer dialog**: "Exports all the presets, amps and IR files so you
   can restore them to this pedal or another pedal at a later date." with Continue/Cancel.
5. Cross-pedal restore is a first-class use case (the export dialog text promises it). A
   model/firmware mismatch between snapshot and connected pedal is a warning in the confirm
   dialog, not a block.

## Write mechanism — byte-exact staged dwrite for all three kinds

Presets restore via the **byte-exact staged dwrite** (chunk 0 name → chunks 1..64 → chunk −1
commit; PROTOCOL.md VERDICT 2026-07-04, ~10 s/slot measured incl. verify), NOT the existing
`DeviceRepository.WritePresetToSlotAsync` param-replay path. Rationale:

- `SlotBlobService` already implements the staged sequence generically over
  `SlotBlobKind(ListPath, Chunks, SlotBytes, …)` with ACK tracking and full read-back verify —
  amps (96×12288) and IRs (32×4096) are just kinds. Restore adds a **preset kind**
  (`root\presets`, 64, 8192). One hardware-verified writer for everything.
- Byte-faithful: the manifest's per-slot SHA verifies the restored bytes exactly (param replay
  regenerates the blob, so SHA equality is not guaranteed).
- No unique-name precondition (param replay's save-by-name needs one) and no dependency on the
  firmware's parameter schema.
- Does not thrash live state (replay writes every param into the live signal chain mid-restore).

`WritePresetToSlotAsync` keeps its existing users (BackupService restore-from-.pst, duplicate);
restore does not touch it.

## Core: `SnapshotRestoreService` (Sonulab.Core/Services)

Sibling of `SnapshotService`. Constructor takes the three `SlotBlobService`-backed seams (preset,
amp, IR) so tests run against `FakeSlotBlobDevice` — **`FakePresetDevice` is not needed and its
ignore-content-dwrites behavior stays untouched**.

### Phase 1 — plan (read-only)

Read + validate the archive (`SnapshotArchive.Read`); read the pedal's three name lists; emit a
90-slot action list:

| Snapshot slot | Pedal slot | Action |
|---|---|---|
| content | anything | `Write` (skipped later if bytes already match) |
| empty | occupied | `Clear` (exact-mirror rule) |
| empty | empty | `Skip` |

The plan is surfaced in the confirm dialog as counts ("writes 81 files, clears 4 slots").

### Phase 2 — execute

Order: **IRs → Amps → Presets** — referenced content lands before the presets that name it, so an
interrupted restore never leaves NEW presets pointing at not-yet-restored amp/IR names.

Per `Write` slot:
1. **Read current pedal content** (unless the safety backup already read it — those blobs are
   reused). This is simultaneously the per-slot backup source and the compare input.
2. **SHA-compare** against the snapshot blob. Equal → count as done, write nothing.
   This makes re-runs after a cancel/failure resume at read speed — resumability for free.
3. Different → per-slot backup file to `Documents\NAMager Backups\Replaced Slots` (existing
   upload convention), then staged write + full read-back verify via `SlotBlobService.UploadAsync`.

Per `Clear` slot: read+backup current content, then `DeleteAsync` (the documented empty-name
`chunk:-1` delete).

Progress: `SnapshotRestoreProgress(SnapshotSlotKind Stage, RestoreSlotPhase Phase, int Done,
int Total)` — `Done` is capture-wide like `SnapshotCaptureProgress` (never resets per stage);
`Total` counts Write+Clear actions. UI renders the export vocabulary:
"Restoring IR files to pedal first — #3 of 81 total files".

Cancellation: checked between slots; the in-flight slot completes its staged write (a staged
sequence must not be abandoned mid-burst — a dread inside a dwrite burst can discard the commit).

Failure: a verify failure or device fault stops the run with the exact slot named. Completed
slots stay verified; re-running restore resumes via the skip rule. `DeviceDisconnectedException`
propagates into the app's existing dead-link state.

IR identities: for every restored IR slot whose manifest entry carries `t3k`, record
`(sha, toneId, modelId)` into the local `IrIndex` — this replaces the one useful behavior of the
removed Import feature.

## App flow

File → **Restore Snapshot…** (replaces Import in the menu; gated on `IsConnected` AND
`WritesAllowed`, and on `FileOperationInFlight` like export):

1. File picker (`.namsnap`).
2. Validate + plan (read-only, seconds). Validation failure → the existing exact-reason dialog.
3. **Confirm dialog**: snapshot summary (model, firmware, captured date, per-kind counts);
   warning line when snapshot device/fw ≠ connected pedal; the exact-mirror statement ("overwrites
   or clears ALL 90 slots to match the snapshot — about N minutes"); checkbox "Back up current
   pedal state first (~3 min)" (default checked); buttons Restore / Cancel. This dialog is the
   explicit-consent gate the repo's device-write rule requires.
4. If checked: safety snapshot via the existing `SnapshotService.CaptureAsync` to
   `Documents\NAMager Backups\pre-restore-<yyyyMMdd-HHmmss>.namsnap`, reusing its read blobs for
   the compare step.
5. Execute with a modal progress dialog (per-file message + progress bar + Cancel), status-bar
   mirroring, `FileOperationInFlight` held for the duration.
6. Done dialog: files written / skipped-identical / cleared, safety snapshot path if taken.
   The preset-usage cache is invalidated (`IPresetUsageService.Invalidate()`) so the usage map
   rescans against the restored content.

## Export explainer dialog

Before the save-file picker in the export flow: title "Export Snapshot", body "Exports all the
presets, amps and IR files so you can restore them to this pedal or another pedal at a later
date. Reading the pedal takes about 3 minutes.", Continue/Cancel. No behavior change beyond the
gate.

## Removal of Import Snapshot

Delete the menu item, `ImportSnapshotFlowAsync`, `MainWindowViewModel.ImportSnapshotAsync`, and
their tests. `SnapshotArchive.Read` stays (restore uses it). IR-identity learning moves into
restore (above).

## Testing

- `SnapshotRestoreServiceTests` (Sonulab.Core.Tests) against three `FakeSlotBlobDevice`s:
  plan actions (write/clear/skip), mirror semantics, skip-if-identical (assert zero staged
  writes for matching slots), execution order (IRs→Amps→Presets), per-slot backup files, verify-
  failure stops with slot identity, cancellation between slots, progress sequence (global counter,
  stage order, phases), IR-identity recording, safety-blob reuse (no double read).
- VM tests: menu gating (disconnected / writes-not-allowed / operation-in-flight), consent-dialog
  plumbing seams, usage-cache invalidation on completion, export explainer gate.
- Preset staged-write kind: unit tests prove `SlotBlobService` with the preset kind speaks the
  exact VERDICT sequence against `FakeSlotBlobDevice(root\presets, 64, 8192)`.

## Hardware validation (new checklist `docs/HARDWARE-VALIDATION-restore.md`)

Named risks the fakes cannot cover:
1. **First in-app use of byte-exact preset dwrite** — HwCheck proved the sequence
   (`--preset-dwrite-probe`); the checklist re-proves it through the app path on one slot before
   a full restore.
2. **Writing the ACTIVE preset slot** — unknown whether the pedal tolerates content replacement
   of the loaded preset mid-restore (audible glitch? stale live state?). Probe explicitly; if it
   misbehaves, mitigation is selecting a different preset first (a `write root\app\preset`
   select), noted as a follow-up.
3. Full-restore timing (estimate: ~10–15 min writes-all; re-run after interrupt ≈ read-speed).
4. Cross-pedal restore onto the second unit if available.

## Timing (estimates from PROTOCOL.md measurements)

| Operation | Cost |
|---|---|
| Plan phase | 3 list reads ≈ 0.3 s |
| Safety snapshot (optional) | ≈ 3 min (read all occupied slots) |
| Preset write | ≈ 10 s/slot (66 writes + verify) |
| Amp write | ≈ 8–11 s/slot (96 chunks + verify read) |
| IR write | ≈ 2–4 s/slot (32 chunks + verify read) |
| Skip-identical | read cost only (≈ 2–5 s/slot; free when safety snapshot ran) |
| Full 30/30/21 restore | ≈ 10–15 min |

## Out of scope (explicitly)

Selective slot restore; restore scheduling/pause-resume UI beyond re-run-and-skip; restoring to
a pedal with different slot geometry (firmware with ≠30 slots); WiFi restore (app is USB-only).
