# CLAUDE.md — StompStation Manager

Desktop app (Avalonia / .NET 10) to manage a **Sonulab StompStation** guitar pedal ("AMP Station",
ESP32-S3, fw 2.5.1) over USB serial — list / select / edit / rename / delete / duplicate / **reorder**
/ backup presets. Replaces the slow VoidX-Control app. Protocol was reverse-engineered from USB/BLE
captures; **`PROTOCOL.md` is the source of truth for the wire protocol.**

## Build / test / run
- Build: `dotnet build`  · Test: `dotnet test` (all should pass; 312 tests)
- Run the app: `dotnet run --project src/Sonulab.App`  (needs VoidX-Control CLOSED — see gotchas)
- Device harness (dev tool, guarded): `dotnet run --project tools/HwCheck [-- --write-test | --reorder-test | --restore <idx> <pst> <name> | --reorder-probe | --list-amps | --upload-amp <vxamp> <slot> [--name <n>] | --delete-amp <slot> | --list-irs | --dump-irs | --upload-ir <irblob> <slot> [--name <n>] | --delete-ir <slot> | --preset-dwrite-probe]`. No args = read-only connect + preset list. Auto-discovers the COM port; `--port COMx` to pin.

## Architecture
- **`src/Sonulab.Core`** (no UI, fully unit-tested): `Protocol/` (SonuCommands, ResponseParser),
  `Model/` (NodeRecord, NodeSchema, PresetDocument=.pst, PresetSlot), `Transport/` (ISonuLink →
  SerialSonuLink / FakeSonuLink; SerialPortStream), `Connection/` (SonuConnector, CompatibilityChecker,
  DeviceSession, FirmwareCatalog), `Services/` (DeviceRepository, ReorderService, BackupService, SlotPlanner, SlotBlobService, AmpService, IrService).
- **`src/Sonulab.Distill`** (no UI, unit-tested): native C# port of the .nam→.vxamp
  distiller (WaveNet runner, WH fitter, vxamp codec, VxampMetadata (SSMD slot-metadata block)). Python `tools/distiller/` is the
  reference oracle; parity goldens via `tools/distiller/make_cs_fixtures.py`.
- **`src/Sonulab.App`** (Avalonia MVVM): ViewModels (Connection, PresetList, AmpList, IrList, ParameterEditor + Block/SubGroup,
  ParameterField, MainWindow), `Views/` (SplitView dashboard + PathIcon icons), `Services/` (LabelService,
  ParameterExposure), `Behaviors/`, embedded `labels.en.json` + `hidden-params.json` + `Icons.axaml`.
- **`tests/`** Sonulab.Core.Tests + Sonulab.App.Tests (xUnit). The faithful `FakePresetDevice` lets the
  full preset/reorder logic be tested offline against realistic firmware behavior.

## Protocol essentials (full detail in PROTOCOL.md)
- Serial: CH340, usually COM6 (a USB replug can re-enumerate it, e.g. COM8 — auto-discovery copes), 115200 8N1. Commands NUL-terminated ASCII; responses CRLF `path:{json}` records.
- Verbs: `read`, `browse` (returns a self-describing schema: type/min/max/options/desc/…), `write`
  (+`"save":"save"`), `dread`, `dwrite`. 30 slots each for presets(8192B)/amp(12288)/ir(4096), chunk 128.
- **Writing a preset = save-from-live**: `write root\app\preset:{"value":"<name>","save":"save"}`.
  `save` targets the slot whose **name** matches → names must be unique. Rename = `dwrite … chunk:-1`.
  `select`+`save` ≈ 216 ms (device copies content); the reorder engine uses this (vs ~12 s param-replay).
  Preset content IS also dwrite-able (PROTOCOL.md VERDICT 2026-07-04: name chunk:0 → chunks 1..64 →
  name chunk:-1 commit; ~10 s/slot) — byte-exact option for restore/duplicate, but save-from-live
  remains the copy engine.

## Critical conventions & gotchas
- **Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia** — it targets Avalonia 11 and
  crashes at runtime on 12. Icons are built-in `PathIcon` geometries, no third-party icon lib.
- **VoidX-Control must be CLOSED** to use the pedal — it holds COM6 exclusively.
- **Opening the port resets the ESP32** (adaptive: OpenSettleMs=250 + up to 8 probe retries @150 ms;
  a true cold boot connects on attempt ~3). **Device names cap ~31 chars.**
- **Device writes are destructive & need explicit user consent**; always back up first (BackupService;
  backups land in `docs/backups/`, gitignored). Reorder/write paths read-back-verify + roll back on failure.
- Parameter editor exposure is a **blocklist** (`hidden-params.json`) so new firmware params auto-appear.
- `.pcapng` captures live in the PARENT dir `..\` (not committed).

## Workflow
superpowers **brainstorming → spec (`docs/superpowers/specs/`) → writing-plans
(`docs/superpowers/plans/`) → subagent-driven-development** (TDD; implement + adversarial review per
task) → merge to `main` (fast-forward) → push. Read `docs/HARDWARE-VALIDATION-*.md` for on-device checks.

## Not done
Amp/IR reorder and backup-all UI deferred from their v1 tabs. (See
`docs/HARDWARE-VALIDATION-amps-tab.md` and `docs/HARDWARE-VALIDATION-plan-dragreorder.md` for
pending manual checks.) Performance pass done — before/after numbers in `docs/perf-findings.md`;
the preset-dwrite question is resolved (VERDICT in PROTOCOL.md; byte-exact restore/duplicate via dwrite is a possible follow-up, not built).
Amp metadata hardware validation (docs/HARDWARE-VALIDATION-amp-metadata.md) pending — run before relying on SSMD blocks on-device. IR-slot metadata not designed.
