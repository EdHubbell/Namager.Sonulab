# StompStation Manager

A fast desktop app (Avalonia / .NET 10) to manage a **Sonulab StompStation** ("AMP Station",
ESP32-S3) guitar pedal over USB serial — list, reorder, duplicate, rename, delete, edit, and
back up presets, fixing the slow VoidX-Control workflow.

## Status
Reverse-engineering / Phase 0 complete; implementation planning next. The full control protocol
has been mapped and validated against real hardware.

## Docs
- [`PROTOCOL.md`](PROTOCOL.md) — the VoidX wire protocol (verbs, framing, slot model, write paths).
- [`docs/PHASE0-protocol-discovery.md`](docs/PHASE0-protocol-discovery.md) — discovery log.
- [`docs/superpowers/specs/2026-06-15-sonulab-control-app-design.md`](docs/superpowers/specs/2026-06-15-sonulab-control-app-design.md) — design spec.
- `docs/probe-output.txt` — full device node-tree dump (generated; gitignored).

## Protocol at a glance
Plaintext over USB serial (CH340, `COM6`, 115200 8N1), BLE, or WiFi. Commands are NUL-terminated
ASCII; responses are CRLF-separated `path:{...}` records. Five verbs: `read`, `browse`, `write`
(+`"save":"save"`), `dread`, `dwrite`. 30 slots each for presets/amps/IRs. Preset content is
written via **save-from-live-state** (save targets the slot matching the name), not `dwrite`.

## tools/ (PowerShell reference scripts; precursors to the C# port)
- `probe.ps1` — read-only: auto-detect port/baud, dump the device node tree.
- `dread_verify.ps1` — read-only: read a preset slot's blob and compare to a `.pst`.
- `write_slot.ps1` — guarded slot write (backup + read-back verify).
- `save_experiment.ps1` — load a `.pst`'s params + save-as to a named slot.

## fixtures/
Real exported `.pst` presets, used as round-trip test fixtures.

## Hardware note
Writing presets changes the pedal's live/active state. The app takes backups before writes and
verifies by read-back. The pedal's `.pcapng` captures live in the parent folder (not committed).
