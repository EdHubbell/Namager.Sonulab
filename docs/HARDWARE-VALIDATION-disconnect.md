# Hardware validation — device disconnect

Requires the pedal on USB and **VoidX-Control CLOSED**. Nothing here writes to an occupied slot
except check 3, which deliberately damages an EMPTY slot — pick one that is empty and expendable.

## 1. HwCheck, read interrupted (exit code)

1. `dotnet run --project tools/HwCheck -- --dump-irs`
2. Pull the USB cable while the dump is streaming.
3. Expect on stderr: `DEVICE LOST: Device disconnected (USB).`
4. Expect exit code `2` (`echo $LASTEXITCODE`).
   Caveat: exit 2 is not unique to device loss — `--dread-arg-probe` and `--pipeline-probe` also
   return 2 when the raw port reopen fails. Confirm the `DEVICE LOST:` line on stderr, not the exit
   code alone. (Not applicable to `--dump-irs`, but the same rule applies to checks 1 and 4.)
5. NOT expected: a raw .NET stack trace, or the process hanging.

## 2. App, read interrupted (dead state)

1. Launch the app, connect, open the Presets tab.
2. Press Refresh and pull the cable while it reads.
3. Expect the status bar to read `Device disconnected — reconnect the pedal and restart NAMager`.
4. Expect the Connect button to be DISABLED (not re-enabled).
5. Expect the app to stay responsive — tabs switch, nothing crashes.
6. Replug the cable; expect the app to stay in the dead state (recovery is a restart, by design).
7. Check `logs/namager.log` — the folder is next to the app binary, i.e.
   `src/Namager.App/bin/Debug/net10.0/logs/namager.log` for a `dotnet run` build (the exact path is
   built in `src/Namager.App/Logging.cs`). Expect one `device link lost (USB)` error entry —
   exactly one, not one per subsequent operation.

## 3. App, upload interrupted (slot attribution)

1. Restart the app, connect, open the Amps tab.
2. Start an upload to a known-EMPTY slot — note which slot you picked; every step below refers to it.
3. Pull the cable once the progress bar shows **Writing**. (An empty slot has no backup stage:
   `SlotBlobService` only backs up an OCCUPIED slot, so progress goes straight to Writing.)
4. Expect a message naming that slot, e.g. for slot 12
   `Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.`
5. Replug, restart the app, and verify the chosen slot's actual state on the device.

## 4. WiFi equivalent

1. `dotnet run --project tools/HwCheck -- --wifi --dump-irs`
2. Interrupt the link mid-dump.
3. Expect `DEVICE LOST: Device disconnected (WiFi).` and exit code `2` (see the caveat in check 1).

**Known limitation — a silent vanish may NOT produce exit 2, and that is not a test failure.**
Detection on WiFi is best-effort. Classification needs the socket to actually fault: killing the AP,
closing the socket, or anything producing a `SocketException`/`IOException` is detected. But if the
pedal simply disappears (power cut with no FIN/RST), the write into the `NetworkStream` is buffered
and succeeds, and `TcpSonuLink` just falls out of its read loop at `MaxWaitMs` with an empty
response. `TimeoutException` is deliberately NOT classified as a disconnect (spec §2), so that shape
produces the OLD behavior: an empty/failed read, no `DEVICE LOST:`, no exit 2.

If a power-cycle gives you a timeout rather than exit 2, record it and move on — to exercise the
detection path, drop the AP or otherwise force a socket reset instead.
