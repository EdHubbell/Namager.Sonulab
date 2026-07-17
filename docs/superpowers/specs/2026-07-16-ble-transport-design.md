# BLE Transport — Design Spec

**Date:** 2026-07-16
**Status:** Approved for planning

## Goal

Connect to the StompStation over **Bluetooth Low Energy** as an automatic fallback when USB serial finds no pedal — same protocol, same features, no new UI. Motivated by a real failure: the pedal's CH340 stopped enumerating on 2026-07-16 while VoidX still connected wirelessly (feedback issue #2).

Out of scope (recorded for later): WiFi/TCP transport (needs live capture of port + provisioning flow), transport picker UI, multiple-pedal disambiguation, BLE on non-Windows.

## Established facts (from PROTOCOL.md + VoidX `app.so` strings, 2026-07-16)

- The pedal exposes the **Nordic UART Service (NUS)**:
  - Service `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`
  - RX (host→device, ATT Write) `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`
  - TX (device→host, Notify) `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`
- **Identical application protocol on all transports**: NUL-terminated ASCII commands in, CRLF-separated `path:{json}` records out, meter stream interleaved on the notify channel. VoidX's own capture did amp uploads over BLE — bulk ops are viable, just slower than USB (accepted).
- No Windows pairing needed (NUS is connect-and-subscribe).
- BLE has no DTR/RTS, so connecting does **not** reset the ESP32 — no settle/probe-retry dance needed.

## Architecture (Approach A)

### New project: `src/Sonulab.Transport.Ble` (`net10.0-windows10.0.19041.0`)

| Unit | Purpose | Depends on |
|---|---|---|
| `BleSonuLink : ISonuLink` | The transport: open (connect + subscribe + single probe), `SendAsync`, close | `IGattPipe`, Core framing helper |
| `IGattPipe` | Thin seam over WinRT: `ConnectAsync`, `WriteFragmentAsync(bytes)`, `NotificationReceived` event, `Disconnect`, negotiated MTU | — |
| `WinRtGattPipe` | Real WinRT implementation (~100 lines; not unit-tested by design) | Windows.Devices.Bluetooth |
| `FakeGattPipe` | Test double (scripted notifications, captured writes, injectable drops) | — |
| `BleDeviceScanner` | `BluetoothLEAdvertisementWatcher` filtered on the NUS service UUID; returns first match within timeout (~4 s); returns nothing cleanly if radio off/absent | Windows.Devices.Bluetooth |

### Core change (small, extraction-proof)

`SonuConnector`/`DeviceSession` currently assume a serial-port factory. They generalize to an **ordered list of link factories**; the serial path becomes entry #1 with behavior unchanged — **existing serial tests must pass unchanged** (same extraction-proof discipline as SlotBlobService). If `SerialSonuLink`'s response reassembly + meter filtering is private, extract it into a shared Core helper both links use; no rewrite.

### App change

- `Sonulab.App` TFM → `net10.0-windows10.0.19041.0` (Windows-only app; installer already win-x64). References `Sonulab.Transport.Ble`.
- `MainWindowViewModel` builds the factory list: **serial first, BLE second**.

## Wire handling

- **Send:** command + trailing NUL, split into ≤MTU fragments (request 247, tolerate the 20-byte default), written sequentially as ATT Write Commands to NUS RX. Protocol payloads contain no embedded NULs, so device-side reassembly is by NUL exactly as over serial.
- **Receive:** TX notifications append to a bounded buffer; meter records (`root\sys\_meters\…`, `root\usb\_status`) are filtered **before** buffering; responses complete on CRLF framing — identical rules to serial.
- **Open:** connect + subscribe (write CCCD) + one probe command (`read root\sys\_name`) validates the pipe. No settle delay.

## Connect flow & UX

- `Connect` button unchanged: serial scan exactly as today → if no pedal answers, BLE scan → connect → same `CompatibilityChecker` gate.
- Status bar names the transport: `AMP Station 2.5.1 — <compat message> (Bluetooth)` / `(USB)`.
- Both fail → `Disconnected (no device found on USB or Bluetooth)`.
- Bluetooth radio off/absent → BLE step skipped silently (logged), serial-only behavior.
- All services (presets/amps/IRs/reorder/backup, Tone3000 handoff) work unchanged — they only see `SonuClient`. WritesAllowed gating and backup-before-write discipline identical.

## Error handling

- Drop mid-command → `SendAsync` faults, `IsOpen` false → existing `Error: …` status path; user reconnects (same contract as USB unplug).
- Scan timeout / radio off → clean "no device found" fallthrough, never a UI exception.
- Notification overflow (bounded buffer exceeded) → fault the link rather than wedge.

## Testing

- **Unit (against `FakeGattPipe`):** fragmentation >MTU; reassembly across split notifications with interleaved meter spam; open+probe; drop-mid-command; bounded-buffer fault. Connector fallback ordering with fake factories (serial-found → BLE never tried; serial-empty → BLE tried).
- **Serial regression:** all existing Core/App tests pass unchanged after the connector generalization.
- **Bench:** `tools/HwCheck --ble` pins the BLE transport (all existing guarded modes work over it).
- **Hardware gate:** `docs/HARDWARE-VALIDATION-ble.md` — BLE connect with USB unplugged; preset list/select/edit; one guarded amp upload (record timing vs USB); fallback-order check (USB plugged in wins); drop test (power off mid-session shows clean error).

## Accepted trade-offs

- Bulk transfers slower over BLE than USB (VoidX does the same; timings recorded during hardware validation).
- Windows-only transport (WinRT); Core stays platform-neutral so a future cross-platform BLE lib could slot in at `IGattPipe`.
- First-found pedal wins; no picker.
