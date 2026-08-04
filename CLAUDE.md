# CLAUDE.md — NAMager for Sonulab

Desktop app (Avalonia / .NET 10) to manage a **Sonulab StompStation** guitar pedal ("AMP Station",
ESP32-S3, fw 2.5.1) over USB serial — list / select / edit / rename / delete / duplicate / **reorder**
/ backup presets. Replaces the slow VoidX-Control app. Protocol was reverse-engineered from USB/BLE
captures; **`PROTOCOL.md` is the source of truth for the wire protocol.**

## Build / test / run
- Build: `dotnet build`  · Test: `dotnet test` (all should pass; 490 tests)
- Run the app: `dotnet run --project src/Namager.App`  (needs VoidX-Control CLOSED — see gotchas)
- Device harness (dev tool, guarded): `dotnet run --project tools/HwCheck [-- --write-test | --reorder-test | --restore <idx> <pst> <name> | --reorder-probe | --list-amps | --upload-amp <vxamp> <slot> [--name <n>] | --delete-amp <slot> | --list-irs | --dump-irs | --upload-ir <irblob> <slot> [--name <n>] | --delete-ir <slot> | --preset-dwrite-probe | --dread-arg-probe | --pipeline-probe | --dswap-probe | --wifi [--ip <addr>]]`. No args = read-only connect + preset list. Auto-discovers the COM port; `--port COMx` to pin. `--wifi` runs any mode over WiFi (mDNS auto-discovery; `--ip <addr>` pins the endpoint, skipping mDNS).

## Architecture
- **`src/Sonulab.Core`** (no UI, fully unit-tested) — Protocol / Model / Transport / Connection /
  Services. `PresetDocument` = the `.pst` on-disk form.
- **`src/Sonulab.Distill`** (no UI, unit-tested): native C# port of the .nam→.vxamp
  distiller (WaveNet runner, WH fitter, vxamp codec, VxampMetadata (SSMD slot-metadata block)). Python `tools/distiller/` is the
  reference oracle; parity goldens via `tools/distiller/make_cs_fixtures.py`.
- **`src/Sonulab.Transport.Wifi`** (no UI, unit-tested; vendor-specific, keeps `Sonulab.*`): WiFi/TCP
  transport for the pedal — `TcpSonuLink` (`ISonuLink` over a persistent socket on port 8080, same wire
  protocol as serial, behind an `ITcpConn` seam), a hand-rolled pure `MdnsMessages` parser (PTR
  `_http._tcp.local`, filtered by TXT `id=voidx`; tested against real captured datagrams), `UdpMdnsQuerier`,
  and `WifiLinkProvider` (an `ILinkProvider`). **NOT wired into the app** — the app is USB-only
  (2026-07-25); this transport is reachable only via `HwCheck --wifi`. Re-enabling = restore the
  `ProjectReference` in `Namager.App.csproj` + the provider entry in `MainWindowViewModel.BuildProviders`.
- **`src/Namager.App`** (Avalonia MVVM): SplitView dashboard, PathIcon icons. Embedded
  `labels.en.json` + `hidden-params.json` + `Icons.axaml` + `Styles/SonulabTheme.axaml` (Studio-warm
  palette tokens & style classes — use tokens, never hex literals in views).
- **`src/Namager.Tone3000`** (no UI, unit-tested): Tone3000 API integration — OAuth PKCE (T3kAuth, publishable key ONLY; the t3k_cs_ secret is never app-readable), DPAPI token store, typed client, downloader. Keys: the publishable key (OAuth client_id, public by design under PKCE) is compiled in as
  `T3kConfig.EmbeddedPublishableKey` so shipped builds sign in with no setup; %APPDATA%\Namager\tone3000.json
  overrides it, and the pre-rename %APPDATA%\StompStationManager dir is still read as a fallback
  (config + token). The t3k_cs_ secret is never in the build (gitignored; template tone3000.json.example). Contract record: docs/tone3000-api-findings.md.
- **`tests/`** The faithful `FakePresetDevice` lets the full preset/reorder logic be tested offline
  against realistic firmware behavior.

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
- **Per-preset output trim** = `root\app\output\pst\level` ("Preset Level", −20…+20 dB, def 0,
  post-everything, saved in the `.pst`). Surfaced as the editor's top `Level` block. `root\app\output`
  itself is the GLOBAL Master block and stays out of `Blocks_InScope`.

## Critical conventions & gotchas
- **Avalonia 12 + built-in `FluentTheme`. Do NOT add FluentAvalonia** — it targets Avalonia 11 and
  crashes at runtime on 12. Icons are built-in `PathIcon` geometries, no third-party icon lib.
- **VoidX-Control must be CLOSED** to use the pedal — it holds COM6 exclusively. This is now
  unconditional: WiFi used to coexist with VoidX (it holds only the COM port), and that was the one
  real capability given up when the app went USB-only.
- **The app is USB-only (2026-07-25).** The WiFi/TCP transport still exists and still passes its 29
  tests, but `MainWindowViewModel.BuildProviders` offers `SerialLinkProvider` alone. WiFi is reachable
  only via `HwCheck --wifi` (mDNS PTR `_http._tcp.local` filtered by TXT `id=voidx`; TCP 8080,
  identical wire protocol) — see `src/Sonulab.Transport.Wifi`, `docs/HARDWARE-VALIDATION-wifi.md`, and
  `docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md` for why: intermittent mDNS, a
  bespoke response-debt resync layer, and no disconnect signal on a silent vanish.
- **Opening the port resets the ESP32** (adaptive: OpenSettleMs=250 + up to 8 probe retries @150 ms;
  a true cold boot connects on attempt ~3). **Device names cap ~31 chars.**
- **Device writes are destructive & need explicit user consent**; always back up first (BackupService;
  backups land in `docs/backups/`, gitignored). Reorder/write paths read-back-verify + roll back on failure.
- **Link death is typed:** transports throw `DeviceDisconnectedException` (Sonulab.Core/Transport) and
  close their own port; `SonuClient` latches the first one, raises `Disconnected` once, and fails all
  later sends instantly. The app enters a dead state (Connect disabled, "reconnect and restart");
  HwCheck prints `DEVICE LOST:` and exits 2 (exit 2 is also a port-reopen failure in
  `--dread-arg-probe`/`--pipeline-probe` — disambiguate on the `DEVICE LOST:` stderr line).
  Reconnect-in-place is deliberately NOT supported — re-opening a live session resets the ESP32 and
  wedges the pedal. **On WiFi (HwCheck only — the app is USB-only) detection is best-effort:** only a real socket fault
  (`SocketException`/`IOException`) is classified. If the pedal silently vanishes (power cut, no
  FIN/RST) the buffered write succeeds and the read just times out — `TimeoutException` is
  deliberately never a disconnect, so that shape falls back to the old behavior.
- Parameter editor exposure is a **blocklist** (`hidden-params.json`) so new firmware params auto-appear.
- `.pcapng` captures live in the PARENT dir `..\` (not committed).
- UI colors come from Styles/SonulabTheme.axaml tokens (Sonulab.*Brush, both theme variants) — never hardcode hex in .axaml; Fluent accent ramp is overridden in App.axaml.

## Workflow
superpowers **brainstorming → spec (`docs/superpowers/specs/`) → writing-plans
(`docs/superpowers/plans/`) → subagent-driven-development** (TDD; implement + adversarial review per
task) → merge to `main` (fast-forward) → push. Read `docs/HARDWARE-VALIDATION-*.md` for on-device checks.

**This repo is public. Specs split by sensitivity:** technical design (protocol, transports, UI,
data formats, RE findings — anything a contributor needs) goes in `docs/superpowers/specs/` and is
published. Anything touching pricing, revenue, partner/vendor strategy (Tone3000, Sonulab),
competitive positioning, or licensing/acquisition thinking goes in the private sibling repo
`../Namager.Strategy/specs/` instead (centralized across all NAMager products). **A plan follows
its spec:** a plan implementing a private spec goes in `../Namager.Strategy/plans/`, not
`docs/superpowers/plans/`, because the task list reconstructs the spec. **Commit messages here
must not summarize a document that lives there** — a public message describing a private doc
leaks it just as effectively. The code itself is GPL and public, so a feature becomes visible
once it's built; what stays private is the reasoning, not the functionality.

## Not done
Current status, shipped-vs-pending, and ranked follow-ups: `docs/STATUS.md`.
