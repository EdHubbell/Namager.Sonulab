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

## Post-fix re-validation (v0.9.2, 2026-07-21 — TcpSonuLink NUL-authoritative)

Fixes the field crash "Slot 23 is empty" when reordering over WiFi (the preset-list read was
truncated by a mid-response idle gap; the reorder command was also unguarded and crashed).

- [x] `HwCheck --wifi` (mDNS) ×3 → each returns the **complete** 25/30 list including the trailing
  slots (idx 22 "Dumble Ford copy", idx 23 "dumm", slot 30 "comp") — no truncation.
- [x] Latency sane: a full connect (mDNS + compat's several reads + list) ran ~9.3 s end-to-end;
  device commands break on the NUL terminator (a no-NUL response would hit MaxWait=2500 ms each,
  which would make compat+list take ~15 s+ — it does not), confirming responses are NUL-terminated.
- [x] **Live WiFi reorder (a WRITE)** via `HwCheck --wifi --reorder-test` (guarded, backup+rollback):
  moved idx 0 → idx 2 and back, **RESULT: REORDER-TEST PASS**, order restored, no crash.
  - Timing (instrumented run): `compat=492ms`, `rotate(select+save)=~2.2s`, and the dominant cost was
    `backed up 3 slots in ~12.6s` — the reorder-*test* uses the safety `MoveAsync` path that reads
    each preset's full 8192-byte blob (64 chunk-reads/slot) for rollback; that's inherent WiFi
    blob-read latency, not the fix. **Zero no-NUL warnings** → no command hit MaxWait, so the
    NUL-authoritative change introduces no latency regression.
  - NOTE: the app's single-slot up/down move uses the lean `MoveStepAsync` (no blob backup) — the
    ~2.2s `rotate` path, not the 12.6s backup. WiFi blob-read speed (backup/duplicate/amp-IR dumps)
    is a separate potential optimization, out of scope for this crash fix.

## App (Ed)
- [ ] Pedal on WiFi, USB unplugged → Connect → status ends "(WiFi)"; presets load
- [ ] Preset select + parameter edit over WiFi (audible on pedal)
- [ ] USB plugged in → Connect → status ends "(USB)" (serial wins the provider order)
- [ ] Pedal off + no USB → Connect → "Disconnected (no device found on USB or WiFi)" after the ~3 s
      browse window; no crash
- [ ] Drop test: connected over WiFi, power the pedal off → next operation shows the Error status;
      power on, Connect reconnects
- [ ] (Optional timing) one guarded amp upload over WiFi vs USB: ____ vs ____ — SUPERVISED
