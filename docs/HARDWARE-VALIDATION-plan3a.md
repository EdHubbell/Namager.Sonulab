# Plan 3a — Manual Hardware Validation (guarded writes)

VoidX-Control CLOSED. The harness writes ONLY to an empty slot and deletes it afterward.

Run: `dotnet run --project tools/HwCheck -- --write-test`

Expect:
1. Connect + Compatibility=Tested, writesAllowed=true.
2. "Duplicating idx S -> empty idx E as 'HW Test'": slot E becomes 'HW Test'.
3. "duplicated content == source (byte-identical)": read-back verify passes.
4. "idx E cleaned up (deleted)": slot E returns to empty.

## Result — 2026-06-16: PASS
```
CONNECTED  name='AMP Station'  ver=2.5.1  arch=ESP32S3  license=stompstation1
Compatibility: Tested  writesAllowed=True
Duplicating idx 0 ('Quad Reverb SM57') -> empty idx 11 as 'HW Test' (~157 params)...
  duplicate took 12418 ms
  OK: idx 11 now 'HW Test'
  OK: duplicated content == source (byte-identical)
  OK: idx 11 cleaned up (deleted)
RESULT: WRITE-TEST PASS
```

## Notes folded into the code
- **Writes get no/near-instant serial response.** `SerialSonuLink` now stops a no-response command at
  `FirstByteTimeoutMs` (default 300 ms) instead of blocking the full `MaxWaitMs`. In practice writes
  returned far faster (full ~157-param duplicate ≈ 12 s), so the device acks/echoes quickly and the
  NUL-stop carries it; the first-byte timeout is only a safety net.
- A full preset write (duplicate/restore/reorder-step) is ~12 s over serial — a deliberate action;
  Plan 3b/Plan 4 should show progress during multi-slot operations.
