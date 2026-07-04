# IR Tab — upload IRs from the app + guarded-write extraction

**Date:** 2026-07-04
**Status:** Approved
**Context:** second follow-up sub-project after 2b (editor polish → **this** → performance).
Carries the deferred refactors from the amps-tab and editor-polish final reviews
(`.superpowers/sdd/progress.md` records them).

## Goal

From the app's IRs tab, a user can pick a `.wav`, watch it convert + upload to an empty IR slot
(guarded, verified), see it in the IR list, rename and delete IRs — mirroring the Amps tab. The
amp/IR write sequence lives in ONE Core implementation. The pedal is available during this work:
two hardware gates run inline.

## What is known / unknown about `root\ir`

Known (PROTOCOL.md): 30 slots, 4096 B payload = 32 chunks of 128 B, `item_type "wav_44100"`,
name table read like amps. Ed uploaded some of `NAMFiles/IR/*.wav` (four dobro IRs) via VoidX —
so dumping the device gives **wav→blob pairs**, the same ground-truth trick that cracked the amp
format.

Unknown until the hardware gates run:
1. **Blob format** (gate 1, read-only dump): float32 samples? int16? XOR-obfuscated like vxamp or
   raw? Sample count/scaling/truncation vs the source `.wav`?
2. **Write semantics** (gate 2, guarded probe): does `root\ir` follow the amp sequence
   (`chunk:0` name → payload → **name at `chunk:-1` = commit**, per-chunk next-expected ACKs)?

Both gates are STOP-and-ask checkpoints: if reality contradicts the hypothesis, implementation
pauses and the finding comes back to Ed before anything is built on it.

## v1 scope (decided in brainstorming)

**In:** list 30 IR slots; upload from `.wav` (converted in-process, verified against the dobro
pairs); upload raw device blob (`.irblob` — restore/copy); inline rename; delete; the deferred
refactors below. **Out:** reorder, backup-all, IR audition/preview, batch upload.

## Architecture

### 1. Core: `SlotBlobService` extraction (the deferred refactor)

The hardware-verified guarded sequence moves from `AmpService` into a new
`src/Sonulab.Core/Services/SlotBlobService.cs`, parameterized by a config record
(`SlotBlobKind(string Path, int Chunks, int SlotBytes, string BackupPrefix)`): `ListAsync`,
`ReadAsync`, `UploadAsync` (backup-if-occupied → ACK-verified chunked write → name-at-`chunk:-1`
commit → read-back verify-or-clear, staged progress), `DeleteAsync`, `RenameAsync`, name
validation. **`AmpService` keeps its exact public API as a thin front** (constants + config) —
its existing tests pass UNCHANGED, which is the acceptance proof that the extraction is
behavior-preserving. `IrService` is a second thin front (`root\ir`, 32 chunks, 4096 B).

**Fake:** `FakeSlotBlobDevice` (parameterized base) replaces the amp-specific internals and gains
an **expected-next-chunk counter**: payload chunks must arrive in order or the ACK reports the
true next-expected (stricter oracle — the amps-tab final review flagged the old fake's
permissiveness). `FakeAmpDevice` remains as a compatibility subclass; existing tests untouched.
A `FakeIrDevice` subclass serves the new tests.

### 2. Distill: `WavToIr` converter

`src/Sonulab.Distill/WavToIr.cs`: minimal PCM WAV reader (16/24-bit int + 32-bit float, mono or
stereo-averaged), resample to 44100 Hz via the existing parity-tested `Resampler`, then
truncate/scale/encode exactly per the format observed in gate 1. Instant — no managed library of
converted files (unlike distilled amps); conversion happens in memory at upload time. Exact
encode rules are written into `docs/ir-format.md` by the analysis task and baked as constants
with provenance comments (the vxamp-format pattern). Verification: converting Ed's source dobro
`.wav`s must reproduce the dumped device blobs within tight tolerance (bit-exact if unobfuscated
float32; tolerance only where resampling is involved).

### 3. HwCheck + hardware gates

- `--dump-irs` (read-only): every occupied IR slot → `NAMFiles/IrDump/NN - <name>.irblob` +
  name table. **Gate 1** runs it on the real pedal, then the analysis task compares dumps to the
  source `.wav`s and writes `docs/ir-format.md`. STOP if no hypothesis fits.
- `--upload-ir <blob> <slot> [--name <n>]` / `--delete-ir <slot>`: thin CLI over `IrService`.
  **Gate 2**: guarded probe on an EMPTY slot — upload a dumped blob, read-back verify, confirm
  the name appears, delete, confirm gone. STOP if the commit semantics differ from amps.
- Existing amp CLI paths are untouched (they ride the AmpService front).

### 4. App: IRs tab

`IrListViewModel` + `IrItemViewModel` + `IrListView` mirroring the Amp versions; the `IRsPage`
stub in `MainWindow.axaml` is replaced the same way `AmpsPage` was (keep `x:Name`);
`MainWindowViewModel` gains `Irs` built on `Connected`. Upload panel differences from amps:
sources are `.wav` / `.irblob`; conversion is instant so there is **no Cancel button** (device
writes are never cancellable by design; there is no long distill phase to cancel); progress is
`Writing chunk n/34` → `Verifying…` → visible `Done`. Same mutual-exclusion gates
(`CanRefresh`/`CanMutate` pattern), empty-slots-only targeting, 31-char unique names, error
surfaces. UI is deliberately a mirror, not a generic VM — the duplication risk worth engineering
away was the protocol layer (§1); two thin VMs are cheaper than a genericized
CommunityToolkit-source-gen VM.

### 5. Editor ride-alongs (from the editor-polish final review)

- Drop `"item"` from the ref-prefetch type filter so both LoadAsync loops agree (the safe
  direction: adding `item` to the field-build loop would render folder nodes as fields). If
  gate 1's browse shows `root\app\ir\ir` is genuinely `item`-typed, that finding comes back to
  Ed instead of silently changing rendering.
- Key the editor's `_expansion` map by block path (invariant) instead of display header.
- Shared JSON string quoting: `JsonString.Quote(string)` in Core (escapes `"` and `\`), used by
  `ParameterFieldViewModel.ToJsonValue`, `DeviceRepository.SelectPresetAsync`/`SaveCurrentAsAsync`
  name paths, and the editor's field writes.

## Error handling

Mirrors the Amps tab table; new rows: unsupported WAV (compressed/malformed → clear message,
nothing written); wrong blob size for `.irblob` (validated by `IrService` per `SlotBlobKind`);
conversion produces silence/clipping — out of scope for v1 (no audio-quality gating; the ear is
the judge, as with amps).

## Testing

- `SlotBlobService`: the existing `AmpServiceTests` pass unchanged (extraction proof); new
  IR-parameterized tests cover the same guarded paths (upload happy/ACK-abort/verify-clear,
  delete, rename, bounds) against `FakeIrDevice`; new ordering tests exercise the
  expected-next-chunk counter (out-of-order write → ACK mismatch → caller aborts).
- `WavToIr`: unit tests with tiny synthetic WAVs (16-bit/24-bit/float32, mono/stereo, 44.1k and
  48k) + the pair gate: dobro `.wav` → blob matches the device dump (fixture copied from the
  dump; corpus-style skip-if-missing).
- VMs: mirror the AmpListViewModel test set minus cancel-during-distill.
- Editor ride-alongs: adjust/extend the editor-polish tests (prefetch filter, expansion key —
  keyed tests updated to block path).
- Hardware: gates 1–2 inline during implementation; `docs/HARDWARE-VALIDATION-ir-tab.md`
  checklist for the full end-to-end eyeball (upload a `.wav` from the app, hear it on the pedal,
  VoidX interop).

## Definition of done

From the IRs tab: pick a `.wav`, watch it convert + upload to an empty slot (guarded, verified),
see it in the list, hear it on the pedal; rename/delete work; `--dump-irs` exists for backups.
One `SlotBlobService` implements the write sequence for both amps and IRs; amp behavior
regression-proven by unchanged tests. Editor ride-alongs landed. Full suite green.
