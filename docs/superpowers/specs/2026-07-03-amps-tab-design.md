# Amps Tab — upload amps from the app (sub-project 2b)

**Date:** 2026-07-03
**Status:** Approved
**Context:** `docs/APP-AMPS-TAB-HANDOFF.md`, `PROTOCOL.md`, `docs/distiller.md`, `docs/vxamp-format.md`

## Goal

From the app's Amps tab, a user can pick a `.nam`, watch it distill and upload to an empty amp slot
(guarded, verified), see it appear in the amp list, and select it on the pedal — no CLI, no VoidX.
Rename and delete work. The Core amp service is unit-tested; the full suite stays green.

## v1 scope (decided in brainstorming)

**In:** list the 30 amp slots; upload from `.nam` (distill → upload); upload from `.vxamp`
(restore / already-distilled); inline rename; delete.
**Out (deferred):** reorder, backup-all, copy-between-slots UI, IR tab, background upload queue.

Decisions:
- **Distiller invocation:** `python` then `py` on PATH; clear error if missing. No settings UI, no bundling.
- **Slot targeting:** empty slots only. To replace an amp: delete, then upload.
- **Naming:** pre-filled from the source file stem, editable before upload, capped at 31 chars (device limit).
- **Distilled artifacts:** kept in a managed library `NAMFiles/Distilled/<amp name>.vxamp` (not temp).
- **Slot backups:** `docs/backups/` (gitignored), matching the existing preset/HwCheck convention.

## Architecture

Two new Core services (no UI dependencies, fully unit-tested), one new app tab mirroring the
Presets tab, and a HwCheck refactor so the amp write sequence has exactly one implementation.

```
.nam ──DistillerService──▶ NAMFiles/Distilled/<name>.vxamp ──┐
.vxamp (picked directly) ────────────────────────────────────┤
                                                             ▼
                              AmpService.UploadAmpAsync(slot, bytes, name, progress)
                              backup → chunked ACK-verified write → commit → read-back verify
                                                             ▼
                                                    pedal root\amp slot
```

### `Sonulab.Core/Services/AmpService.cs`

Owns `root\amp`, mirroring `DeviceRepository`'s shape (thin class over `SonuClient`). Constructor
takes `SonuClient` and a backup directory path.

Constants: `SlotCount = 30`, `AmpChunks = 96`, `AmpBytes = 12288`, `NameMaxChars = 31`.
New model record `AmpSlot(int Index, string Name)` with `IsEmpty` (separate from `PresetSlot`; the
types may diverge).

- `ListAmpsAsync()` → 30 `AmpSlot`s from `ReadListAsync(@"root\amp")`, padded to 30 like presets.
- `ReadAmpAsync(index)` → 12288-byte blob via `DReadBlobAsync(@"root\amp", index, 96)`.
- `UploadAmpAsync(int slot, byte[] vxampBytes, string name, IProgress<AmpUploadProgress>? progress, CancellationToken ct)`:
  1. **Validate:** exactly 12288 bytes; name non-empty ASCII, trimmed, ≤31 chars.
  2. **Backup:** if the name table shows the slot occupied, dread the blob to
     `docs/backups/amp-<slot>-<timestamp>.vxamp`. **Skip the dread entirely for empty slots**
     (a 96-chunk dread of an empty slot is 96 timeouts).
  3. **Write** (the hard-won sequence from HwCheck / `PROTOCOL.md`):
     `chunk:0` = name (ASCII, zero-padded to 128 B) → `chunk:1..96` = payload, 128 B each →
     **`chunk:-1` = the name again — this is the COMMIT** (all-zeros there deletes the slot and
     discards the upload). After every chunk, parse the device ACK
     (`dwrite root\amp:{"index":N,"chunk":<nextExpected>}`) and abort on missing/mismatched ACK.
     An abort before the commit chunk leaves the slot uncommitted — no cleanup needed.
  4. **Read-back verify:** dread 96 chunks, byte-compare against the intended payload. On mismatch,
     clear the slot (zeros at `chunk:-1`) so a corrupt amp is never left selectable, then throw
     with diagnostics (first differing offset/chunk).
  5. **Progress** stages reported: `BackingUp`, `Writing` (with chunk n / 98), `Verifying`, `Done`.
- `DeleteAmpAsync(slot)` — backup the blob first, then zeros at `chunk:-1` (HwCheck's tested sequence).
  No-op if the slot is already empty.
- `RenameAmpAsync(slot, name)` — padded name at `chunk:-1`, same as preset rename.
  **Untested on amp hardware** — goes on the manual hardware-validation checklist.

### `Sonulab.Core/Services/DistillerService.cs`

- Locates Python by trying `python`, then `py`, on PATH. Locates the script by searching upward
  from `AppContext.BaseDirectory` for `tools/distiller/distill.py` (the app is a dev tool run from
  the repo). Both overridable via constructor for tests.
- `DistillAsync(namPath, outVxampPath, ct)` → runs
  `python tools/distiller/distill.py "<nam>" "<out>"`, captures stdout/stderr. Success = exit code 0
  **and** a 12288-byte output file. Failure throws `DistillerException` carrying the stderr tail.
  Cancellation kills the subprocess.
- Subprocess execution behind a small `IProcessRunner` interface so the service is unit-testable
  without Python installed.

### HwCheck refactor

`--upload-amp`, `--delete-amp`, `--list-amps`, `--dump-amps` re-implemented on top of `AmpService`.
CLI output/behavior stays equivalent (it is the hardware-validation harness). Only correct
implementation of the write sequence lives in Core.

## App UI (`Sonulab.App`)

Mirrors the Presets tab: `AmpListViewModel` + `AmpItemViewModel` (ViewModels/), `AmpListView.axaml(.cs)`
(Views/). `MainWindowViewModel` gains an `Amps` property constructed alongside `Presets` with the same
`writesAllowed` gating. The `AmpsPage` stub in `MainWindow.axaml` is replaced by the view; the nav
ListBox / page-visibility toggle in `MainWindow.axaml.cs` is extended for it.

**List:** 30 rows, slot number + name or "(empty)", styled like `PresetListView`. Per-item inline
rename (edit-in-place pattern from presets, 31-char cap in the field) and delete. Toolbar: Refresh,
**Upload from .nam…**, **Upload .vxamp…**.

**Upload flow** (both entry points converge):
1. Toolbar button → OS file picker (Avalonia `StorageProvider` from the view's `TopLevel`; first
   use in the app; `.nam` / `.vxamp` filters). No new dependencies.
2. Inline upload panel appears at the bottom of the tab (no modal windows): source file name,
   editable **Name** (pre-filled from file stem, truncated to 31), **Slot** dropdown of empty slots
   only (pre-selected to the first empty), **Start**, **Cancel**.
3. Start: `.nam` → distill to `NAMFiles/Distilled/<name>.vxamp`, then upload; `.vxamp` → upload
   directly. Progress bar + stage text: `Distilling… (~30 s)` → `Writing chunk n/98` →
   `Verifying…` → `Done — '<name>' in slot N`. List refreshes; the new row is selected.
4. **Cancel** enabled during distill only (kills the subprocess; nothing written to the device);
   disabled once device writes begin.
5. Panel and all write actions disabled when writes aren't allowed or while any operation runs
   (existing `IsBusy` pattern).

## Error handling

| Failure | Behavior |
|---|---|
| Python not found | Panel message: "Python not found on PATH — install Python 3 with numpy/scipy." |
| Distill non-zero exit / bad output | Panel shows the stderr tail from `DistillerException`. |
| `.vxamp` wrong size | Validation message before any write. |
| No empty slots | Upload buttons show a message; panel won't open. |
| Name collides with an existing amp | Validation message before any write (names should stay unique). |
| Name >31 chars | Truncated in the field; can't start with an empty name. |
| ACK missing/mismatch mid-write | Abort before commit → slot uncommitted; error with the last chunk diagnostic. |
| Read-back verify fails | Slot cleared (never leave a corrupt amp selectable); error with first-diff diagnostics. |
| Device disconnect | Existing connection-layer handling surfaces it; operation fails cleanly. |
| VoidX-Control holding the port | Existing ConnectionViewModel "close VoidX" message (connect-time, unchanged). |

## Testing

- **Fake device:** extend `FakePresetDevice`/`FakeSonuLink` with a faithful `root\amp` node —
  name table, per-chunk ACK with next-expected semantics, `chunk:-1` commit (name) / delete (zeros)
  behavior, dread of stored blobs, timeout behavior for empty-slot dreads.
- **`AmpService` tests:** list; happy-path upload (chunk order, name at 0 and −1, ACK pacing);
  validation failures (size, name); ACK-mismatch abort leaves slot uncommitted; verify-fail clears
  the slot; empty-slot upload skips the backup dread; occupied-slot upload writes a backup file;
  delete (backs up, clears, no-op on empty); rename.
- **`DistillerService` tests** (fake `IProcessRunner`): python missing → clear error; non-zero
  exit → stderr in exception; output wrong size → error; success path; cancellation kills process.
- **`AmpListViewModel` tests:** panel state transitions (pick → configure → progress → done/error),
  empty-slot dropdown contents, name pre-fill/truncation, writes-gating.
- **Suite:** all existing tests (146) plus the new ones stay green: `dotnet build` && `dotnet test`.
- **Manual hardware checks** (deferred to a bench session, documented in a
  `docs/HARDWARE-VALIDATION-amps-tab.md` checklist): real `.nam` end-to-end upload from the app,
  amp rename on hardware (`chunk:-1` rename is untested for amps), delete, verify audible result.

## Definition of done

From the app's Amps tab: pick a `.nam`, watch it distill + upload to an empty slot (guarded,
verified), see it in the amp list, select it on the pedal — no CLI, no VoidX. Rename/delete work.
`AmpService`/`DistillerService` unit-tested; HwCheck shares the Core implementation; app builds and
runs; full suite green.
