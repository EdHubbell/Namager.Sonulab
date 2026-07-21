# Manual validation — WiFi transport (SP1)

Live bench results are under "Bench (verified)". Ed's remaining GUI checks are under "App (Ed)".

## Bench (verified 2026-07-21, pedal "AMP Station" fw 2.5.1 on "Duke Park Mesh")

Run read-only via `HwCheck --wifi` (mDNS auto-discovery) from `C:\Development\ToneManager`. No device
writes were performed.

- [x] `HwCheck --wifi` (mDNS) → **CONNECTED** `name='AMP Station' ver=2.5.1 arch=ESP32S3
  license=stompstation1`; compatibility `Tested writesAllowed=True`; **25/30 presets listed**; `RESULT:
  read-only PASS`. Reproduced 3/3 consecutive runs.
- [x] `HwCheck --wifi --browse root\sys\wifi` → **BROWSE COMPLETE (4 records)** read-only: the `wifi`
  vfolder, `ssid = "Duke Park Mesh"`, `password = <redacted — not committed>`, `state`. Confirms
  arbitrary read-only browse works over the TCP transport.
- [x] mDNS discovery correctly returns **NOT CONNECTED** when the pedal is absent from WiFi (observed
  during the window when the pedal had re-homed to USB and its old DHCP lease had expired), and finds it
  again once it rejoins — validating the "answers intermittently, re-send the query every ~2 s" design.
- [ ] `HwCheck --wifi --ip <addr>` (pinned): not re-validated this run — the previously-pinned
  `192.168.8.241` lease had expired (DHCP reassigned), so mDNS auto-discovery was used instead. The
  `--ip` path is exercised by unit tests (`WifiLinkProvider.ForKnownEndpoint`); pin the *current* lease
  to spot-check if desired.
- [ ] Connect times (PERF): the `PERF connect transport=WiFi …` line is emitted to the NLog **file**,
  not the console, so wall-clock ms were not captured in this run. Discovery + connect completed well
  within the 6 s mDNS window on every successful attempt.

### Notes / observations
- `root\sys\wifi\state` read back `DISCONNECTED` even though the pedal was reachable over WiFi at the
  time — likely the STA-interface field rather than the mesh/AP it is actually joined to. Recorded as an
  observation; it did not affect connect/identify/browse. Not investigated further (read-only session).
- The pedal's WiFi credentials are visible over the read-only browse; **never commit them.** They are
  redacted here.

## App (Ed)
- [ ] Pedal on WiFi, USB unplugged → Connect → status ends "(WiFi)"; presets load
- [ ] Preset select + parameter edit over WiFi (audible on pedal)
- [ ] USB plugged in → Connect → status ends "(USB)" (serial wins the provider order)
- [ ] Pedal off + no USB → Connect → "Disconnected (no device found on USB or WiFi)" after the ~3 s
      browse window; no crash
- [ ] Drop test: connected over WiFi, power the pedal off → next operation shows the Error status;
      power on, Connect reconnects
- [ ] (Optional timing) one guarded amp upload over WiFi vs USB: ____ vs ____ — SUPERVISED
