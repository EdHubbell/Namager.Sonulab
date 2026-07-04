# Handoff — Sub-project 2b: Amps tab in Sonulab.App (upload amps from the app)

**For the agent picking this up.** The goal: let a user manage and **upload amps to the pedal from the
app's Amps tab** — including converting a native `.nam` and uploading it. The offline distiller and the
on-device upload both work today (verified by ear on hardware). This sub-project is the UI + integration
that ties them together in `Sonulab.App`.

**Start with `superpowers:brainstorming`** (this repo's convention: brainstorm → spec in
`docs/superpowers/specs/` → `writing-plans` → `subagent-driven-development`). Do NOT jump to code — nail
the v1 scope with the human first (see "Decisions to brainstorm" below).

---

## Where things stand (context)

Two sub-projects are complete and merged to `main`:
1. **Format RE** (`tools/vxamp-re/`, `docs/vxamp-format.md`): the device amp format is fully cracked —
   float32 weights XOR-obfuscated in a Wiener–Hammerstein FIR-cascade TLV container.
2. **The distiller** (`tools/distiller/`, `docs/distiller.md`): converts a `.nam` → `.vxamp`, fitting the
   device model to the NAM's response, calibrated to match VoidX (10/14 clear wins + near-ties, never
   clearly worse; loudness-normalized). **Validated by ear** — a distilled amp on the pedal sounds
   identical to the source NAM.
3. **On-device upload works** (`tools/HwCheck --upload-amp`, fix in commit `eda05db`): the write path is
   solved (see the protocol note below). `RESULT: UPLOAD-AMP OK`, verified 3× on hardware.

So the pipeline `.nam → distill → .vxamp → upload → pedal` works from the CLI **today**. 2b makes it a
first-class app feature.

## The main task: build out the Amps tab

The app already has an **Amps** nav item whose page is a stub (`MainWindow.axaml`:
`AmpsPage` = "Amp list — coming soon", `IsVisible=False`; nav switching is in `MainWindow.axaml.cs`).
Build it out, mirroring the **Presets** tab, which is the working reference pattern.

### Likely v1 features (confirm scope in brainstorming)
- **List** the 30 amp slots (names from `root\amp`) with occupied/empty state — mirror `PresetListView`.
- **Upload from a `.nam`**: pick a `.nam` file → run the distiller → upload the resulting `.vxamp` to a
  chosen (empty) slot. Guarded: back up the slot, write, read-back verify, surface progress + result.
- **Upload from a `.vxamp`**: for already-distilled/backed-up blobs (restore, copy between slots).
- **Manage**: rename, delete (both already work at the protocol level — see HwCheck `--delete-amp`).
- Optional (defer if scope grows): reorder, backup-all, IR tab.

## Architecture — two structural moves that matter

1. **Lift the amp upload/list/delete logic from `tools/HwCheck/Program.cs` into `Sonulab.Core`.** Right
   now the *only* correct implementation of the amp write sequence lives in the HwCheck CLI. Do NOT
   duplicate it in the app. Create an `AmpService` (or extend `DeviceRepository`) in
   `src/Sonulab.Core/Services/` with methods like `ListAmpsAsync`, `UploadAmpAsync(slot, vxampBytes,
   name)` (backup + write + verify), `DeleteAmpAsync`, `RenameAmpAsync` — unit-tested against the
   `FakePresetDevice`/`FakeSonuLink` harness (extend it for amp slots). Then refactor HwCheck to call the
   Core service too, so both share one implementation. `DeviceRepository` already does exactly this shape
   for presets (`root\presets`) — copy that structure for `root\amp`.

2. **Distiller integration = subprocess, not a port.** The distiller is ~700 lines of numpy/scipy DSP;
   porting to C# is a large, risky effort with no benefit for v1. The app should shell out:
   `python tools/distiller/distill.py "<nam path>" "<out.vxamp>"` (it writes a 12288-byte `.vxamp`), then
   upload that via the Core amp service. Brainstorm with the human how to locate Python (PATH? a
   configured path? bundle?) — the user has Python installed (this is a dev-tool app on their machine), so
   "invoke `python`/`py` on PATH, surface a clear error if missing" is likely fine for v1.

## Key technical facts (don't rediscover these)

- **The amp upload sequence (hard-won — see `PROTOCOL.md` and `docs/AMP-UPLOAD-DEBUG-HANDOFF.md`):**
  `dwrite root\amp:{"index":N,"chunk":C,"value":"<hex>"}` — `chunk:0` = name (ASCII, zero-padded to
  128 B); `chunk:1..96` = the 12288-byte payload, 128 B/chunk; **`chunk:-1` = the NAME AGAIN** — this is
  the COMMIT (all-zeros there deletes the slot and discards the upload — the original bug). The device
  ACKs each chunk with `dwrite root\amp:{"index":N,"chunk":<nextExpected>}`; verify the ACK and abort on
  mismatch. `SonuClient.DWriteChunkAsync` now returns the raw response so callers can check the ACK.
- **Reading amps** already works: `SonuClient.ReadListAsync("root\amp")` (names), `DReadBlobAsync(
  "root\amp", idx, 96)` (the 12288-byte blob). Skip the backup-dread for a slot the name table shows empty
  (a 96-chunk dread of an empty slot is 96 timeouts).
- **Distiller CLI:** `python tools/distiller/distill.py "<nam>" "<out.vxamp>"` → 12288-byte `.vxamp`. It
  handles resampling (48k→44.1k), the FIR fit, the provisional nonlinearity, and loudness normalization.

## Device gotchas (from `CLAUDE.md` — all apply to the app's write paths)

- **VoidX-Control must be CLOSED** — it holds the COM port exclusively. The app already handles connect;
  surface a clear "close VoidX" message on connect failure (the ConnectionViewModel pattern exists).
- **Opening the port resets the ESP32** (`OpenSettleMs≈1500` + probe retries) — already handled by the
  connection layer; reuse it.
- **Device names cap ~31 chars.** Amp names too.
- **Writes are destructive → always back up first + read-back verify + roll back on failure.** The amp
  service must follow the same guarded-write discipline as the preset paths (`BackupService`,
  `ReorderService` are the models). Amp backups land in `docs/backups/` (gitignored).
- **UI stack: Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia** (it targets Avalonia 11
  and crashes at runtime). Icons are built-in `PathIcon` geometries (`Icons.axaml`). No third-party icon
  libs.

## App code map (mirror the Presets tab)

- `src/Sonulab.App/ViewModels/PresetListViewModel.cs` + `PresetItemViewModel.cs` → make
  `AmpListViewModel` + `AmpItemViewModel`.
- `src/Sonulab.App/Views/PresetListView.axaml`(`.cs`) → make `AmpListView`.
- `src/Sonulab.App/ViewModels/MainWindowViewModel.cs` exposes `Presets`/`Editor` → add `Amps`.
- `src/Sonulab.App/Views/MainWindow.axaml` (+ `.axaml.cs`) — replace the `AmpsPage` stub with the
  `AmpListView`; the nav ListBox + page-visibility toggle logic is in `MainWindow.axaml.cs`.
- Core: `src/Sonulab.Core/Services/DeviceRepository.cs` (preset pattern), `BackupService.cs`,
  `ReorderService.cs`; `SonuClient.cs` (`ReadListAsync`, `DReadBlobAsync`, `DWriteChunkAsync`,
  `SendRawAsync`). Tests: `tests/Sonulab.Core.Tests` with `FakePresetDevice`/`FakeSonuLink` (extend for amps).
- Build/test/run: `dotnet build` · `dotnet test` (146 tests today) · `dotnet run --project src/Sonulab.App`.

## Decisions to brainstorm with the human first

1. **v1 scope**: list + upload-from-.nam + delete/rename only? Or also reorder/backup/copy? (Recommend
   the minimal upload-focused v1; defer reorder.)
2. **Distiller invocation**: `python` on PATH vs a configurable path vs bundling. (Recommend PATH + clear
   error for v1.)
3. **Upload UX**: progress reporting for the ~3s upload (98 ACK-paced chunks); slot picker (empty slots
   only, or overwrite-with-confirm); what name to give the amp (file stem, ≤31 chars, editable).
4. **Where distilled `.vxamp` files go** (temp dir vs a managed library the user keeps).

## Backlog — other open items (not required for 2b, but track them)

- **Preset content dwrite may actually work now.** Sub-project 1 concluded preset *content* isn't
  `dwrite`-able (2026-06-15) — but that test used zeros at `chunk:-1`, the same bug just fixed. With the
  corrected terminator (name at `chunk:-1`) it may work, which would let the **reorder engine** write
  content directly instead of the slow save-from-live param replay. One guarded re-test settles it. See
  the flag in `PROTOCOL.md`.
- **Nonlinearity is provisional** (v1 distiller = clean→edge-of-breakup). Pushing into real
  breakup/high-gain needs a synthetic-NAM generator + controlled VoidX captures to pin the exact
  waveshaper (procedure in `tools/distiller/FINDINGS.md ## Task 4`). High-gain may hit a device ceiling
  regardless.
- **IR upload** (`root\ir`, 32 chunks) likely follows the same name-at-`chunk:-1` pattern — untested. An
  IRs tab is the natural sibling of the Amps tab.
- **VoidX inverts output polarity on ~half its conversions** (RE finding) — only matters if amps are ever
  A/B'd/blended on-device.

## Definition of done (2b v1)

From the app's Amps tab, a user can pick a `.nam`, watch it distill + upload to an empty slot (guarded,
verified), see it appear in the amp list, and select it on the pedal — no CLI, no VoidX. Rename/delete
work. Unit-tested Core amp service; app builds and runs; full suite green.
