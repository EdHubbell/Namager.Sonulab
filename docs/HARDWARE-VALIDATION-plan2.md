# Plan 2 — Manual Hardware Validation

Run with the pedal on USB and **VoidX-Control CLOSED** (it holds COM6 open exclusively).

Harness: `tools/HwCheck` (console app referencing `Sonulab.Core`). Run:
```
dotnet run --project tools/HwCheck            # defaults to COM6
dotnet run --project tools/HwCheck -- COM6
```
It drives the REAL `SystemSerialPort` → `SonuConnector` → `DeviceSession` → `CompatibilityChecker`,
then `ReadListAsync(root\presets)`. Read-only — no writes.

## Checklist
1. **Connect** via `SonuConnector(() => new SystemSerialPort(), new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 })`, ports `["COM6"]`, baud `[115200]` → non-null link.
2. **Identify**: `Device.Name == "AMP Station"`, `Version` populated.
3. **Compatibility**: with `2.5.1/ESP32S3/stompstation1` tested → `Tested`, `WritesAllowed == true`.
4. **Read-only list**: `ReadListAsync(root\presets)` returns 30 slots matching `docs/probe-output.txt`.
5. **No writes** in this task.

## Result — 2026-06-15: PASS
```
CONNECTED  name='AMP Station'  ver=2.5.1  arch=ESP32S3  license=stompstation1
           id=c7e811051914272110b41dc7c558
Compatibility: Tested  writesAllowed=True
ReadListAsync(root\presets): 15/30 in use
   slots 1-11,13-15 as in probe-output.txt, plus slot 26: "Test Write"
```

## Hardware findings folded into the code
- **ESP32 open-reset:** opening the CH340 resets the MCU; the first command is lost. Handled by
  `OpenSettleMs` (≈1500) + `ProbeAttempts` (≈3) in `SonuConnector`.
- **NUL-terminated responses:** the device ends each response with `\0`. `SerialSonuLink` now stops
  reading at the NUL (deterministic, size-independent); the idle-gap is only a fallback.
- **Avoid `browse root` over serial:** it is a ~30 KB / multi-second dump; truncating it corrupts the
  next command's response. `CompatibilityChecker` browses the three list nodes directly instead.
- **No idle meter stream over serial** (unlike BLE) — the channel is silent until queried.
```
