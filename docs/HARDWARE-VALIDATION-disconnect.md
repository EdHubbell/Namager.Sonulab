# Hardware validation — device disconnect

Requires the pedal on USB and **VoidX-Control CLOSED**. Nothing here writes to an occupied slot
except check 3, which deliberately damages an EMPTY slot — pick one that is empty and expendable.

## 1. HwCheck, read interrupted (exit code)

1. `dotnet run --project tools/HwCheck -- --dump-irs`
2. Pull the USB cable while the dump is streaming.
3. Expect on stderr: `DEVICE LOST: Device disconnected (USB).`
4. Expect exit code `2` (`echo $LASTEXITCODE`).
5. NOT expected: a raw .NET stack trace, or the process hanging.

## 2. App, read interrupted (dead state)

1. Launch the app, connect, open the Presets tab.
2. Press Refresh and pull the cable while it reads.
3. Expect the status bar to read `Device disconnected — reconnect the pedal and restart NAMager`.
4. Expect the Connect button to be DISABLED (not re-enabled).
5. Expect the app to stay responsive — tabs switch, nothing crashes.
6. Replug the cable; expect the app to stay in the dead state (recovery is a restart, by design).
7. Check `tonemanager.log` for one `device link lost (USB)` error entry — exactly one, not one per
   subsequent operation.

## 3. App, upload interrupted (slot attribution)

1. Restart the app, connect, open the Amps tab.
2. Start an upload to a known-EMPTY slot.
3. Pull the cable once the progress bar is past the backup stage and into Writing.
4. Expect a message naming that slot, e.g.
   `Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.`
5. Replug, restart the app, and verify slot 12's actual state on the device.

## 4. WiFi equivalent

1. `dotnet run --project tools/HwCheck -- --wifi --dump-irs`
2. Power-cycle the pedal (or disable the AP) mid-dump.
3. Expect `DEVICE LOST: Device disconnected (WiFi).` and exit code `2`.
