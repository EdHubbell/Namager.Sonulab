# WiFi (TCP + mDNS) Transport — Design Spec

**Date:** 2026-07-17
**Status:** Approved for planning (Sub-project 1 of 2)

## Goal

Connect to the StompStation over **WiFi** — mDNS auto-discovery + a raw TCP socket speaking the
existing protocol — as an automatic fallback after USB. Motivated by the pedal defaulting to WiFi
in VoidX and by USB-port hardware flakiness (feedback issue #2, bad USB-A port).

**This is Sub-project 1: discover-and-connect only.** Provisioning (writing WiFi creds to the pedal)
is Sub-project 2 — a separate spec — because it involves **device writes that could strand the pedal
off-network with no USB data path**, so it must run supervised, never unattended. Also out of scope:
BLE (shelved plan `2026-07-16-ble-transport.md`), manual-IP entry in the app UI (mDNS-only per the
design decision; `HwCheck --ip` exists for bench testing only).

## Confirmed facts (live pedal, 2026-07-17 — full detail in PROTOCOL.md)

- **Raw TCP, port 8080**, identical NUL-terminated protocol to USB. `read root\sys\_name\0` →
  `root\sys\_name:{"value":"AMP Station"}\0`. One **persistent socket** carries a whole session;
  `browse root` returned the full ~35 KB tree over it.
- **No continuous meter stream** over TCP (unlike USB). Meter filtering kept anyway (cheap, safe).
- **First command after connect can return an empty record** — same quirk class as USB's
  reset-eats-first-command; handled by probe-retry (`ProbeAttempts`), no settle delay needed.
- **mDNS**: query `_http._tcp.local` (PTR) on `224.0.0.251:5353`. Pedal instance =
  `voidx<deviceId>._http._tcp.local` (deviceId = `root\sys\_id`); **SRV** → port 8080, **A** → IP;
  **TXT** carries `id=voidx` (reliable pedal filter vs. other `_http._tcp` advertisers), `MAC=…`,
  `name=AMP Station`.

## Architecture

### Shared foundation (transport-agnostic — also unblocks the shelved BLE plan)

- `interface ILinkProvider { string Name { get; } Task<ISonuLink?> TryConnectAsync(CancellationToken); }`
  — a way to reach the pedal; `Name` is the transport label ("USB", "WiFi").
- `static class LinkProbe { Task<bool> VerifyAsync(ISonuLink, CancellationToken); }` — the shared
  identity probe (`read root\sys\_name` → matching record), extracted from `SonuConnector`.
- `SerialLinkProvider : ILinkProvider` (`Name="USB"`) — wraps the existing serial scan, and
  **enumerates COM port names fresh on every attempt** (fixes the stale-snapshot bug that hid a
  replugged pedal on a new COM number).
- `DeviceSession` iterates an **ordered `IReadOnlyList<ILinkProvider>`**; returns the winning
  transport in `SessionState.Transport`. Existing serial behavior and its tests unchanged
  (extraction-proof); `ConnectionViewModel`/`MainWindowViewModel`/`HwCheck` adapt.

### WiFi transport (`src/Sonulab.Transport.Wifi`, `net10.0`)

Platform-neutral (plain BCL sockets — no WinRT, unlike BLE), so it stays in a lean project and
Core stays clean.

| Unit | Purpose | Depends on |
|---|---|---|
| `TcpSonuLink : ISonuLink` | Persistent TCP socket; command+NUL out, collect-until-NUL in (same policy/defaults as `SerialSonuLink`); bounded receive | `ITcpConn` seam |
| `ITcpConn` / `SystemTcpConn` | Thin seam over `System.Net.Sockets` (connect/send/receive/close) so `TcpSonuLink` is unit-testable with a fake | — |
| `MdnsRecord` (record) | Parsed pedal advertisement: `InstanceName`, `Host`, `IPAddress`, `Port`, `TxtId`, `DeviceName` | — |
| `MdnsResponseParser` (static, pure) | Bytes → `MdnsRecord?`; unit-tested with the **real captured packet bytes** | — |
| `IMdnsQuerier` / `UdpMdnsQuerier` | Send the `_http._tcp.local` PTR query, gather raw response datagrams within a timeout; never throws (radio/socket errors → empty) | `MdnsResponseParser` |
| `WifiLinkProvider : ILinkProvider` | `Name="WiFi"`: discover (filter TXT `id=voidx`) → open `TcpSonuLink` → `LinkProbe` verify | `IMdnsQuerier`, `TcpSonuLink`, `LinkProbe` |

### App + tooling

- `Sonulab.App` provider order: **USB → WiFi** (`WifiLinkProvider` with a ~3 s discovery timeout).
  Status bar: `"{name} {version} — {message} ({transport})"`; both-fail:
  `"Disconnected (no device found on USB or WiFi)"`. `Sonulab.App` stays `net10.0` (WiFi project is
  platform-neutral — no TFM change, unlike the BLE plan).
- `tools/HwCheck --wifi [--ip <addr>]` — run any existing mode over WiFi; `--ip` pins a known IP
  (skips mDNS) for deterministic bench testing against `192.168.8.241`.

## Data flow (connect)

1. `DeviceSession.ConnectAsync` → `SerialLinkProvider.TryConnectAsync` (fresh COM scan). If a pedal
   answers, done (`Transport="USB"`).
2. Else `WifiLinkProvider.TryConnectAsync`: `UdpMdnsQuerier` sends the PTR query, collects datagrams
   for the timeout, `MdnsResponseParser` yields the first record with TXT `id=voidx`; open
   `TcpSonuLink` to its IP:port; `LinkProbe.VerifyAsync` (with retries for the empty-first-command
   quirk). Success → `Transport="WiFi"`.
3. Neither → `SessionState(false, …)`.

## Error handling

- No pedal on WiFi / mDNS blocked / no network → querier returns empty → provider returns null →
  clean fallthrough to "no device found on USB or WiFi" (never a UI exception).
- TCP drop mid-command → `SendAsync` faults, `IsOpen` false → existing `Error: …` status; reconnect.
- Receive buffer overflow (bounded) → fault + close the link (defensive; matches BLE design).
- A provider throwing (e.g. no network stack) is caught by `DeviceSession` and the scan continues.

## Testing

- **Unit (fakes, no real network):** `MdnsResponseParser` against the real captured bytes (pedal
  record + the Canon-printer decoy → parser picks the `id=voidx` one); `TcpSonuLink`
  fragmentation-free send + NUL-terminated collection, split-response reassembly, no-response
  (write) first-byte timeout, buffer-overflow fault, send-on-closed; `WifiLinkProvider` found /
  not-found / failed-probe against a fake querier + fake conn; connector fallback ordering.
- **Serial regression:** all existing Core/App tests pass unchanged after the foundation extraction.
- **Live bench (against 192.168.8.241, this session):** `HwCheck --wifi --ip 192.168.8.241` connects
  + lists presets; `--wifi` (mDNS) discovers + connects; app connects with status "(WiFi)"; a preset
  select + parameter read over WiFi. Recorded in `docs/HARDWARE-VALIDATION-wifi.md`.

## Accepted trade-offs / notes

- mDNS is hand-rolled (one-shot query + pure parser) rather than a NuGet dependency — matches the
  project's minimal-dependency stack preference; the parser (the only tricky part) is fully unit-tested.
- One pedal assumed (first `id=voidx` match wins) — multi-pedal disambiguation is YAGNI.
- Pedal runs one transport at a time (`wifi\state` was DISCONNECTED on USB); the provider order means
  USB always wins when both are somehow present, which is the desired precedence.
