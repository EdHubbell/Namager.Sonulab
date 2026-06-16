# Plan 3b — Manual Hardware Validation (guarded reorder) — PASS

VoidX-Control CLOSED. Each shifted slot replays ~157 params, so a small move takes ~a minute.

Run: `dotnet run --project tools/HwCheck -- --reorder-test`

## STATUS: PASS (2026-06-16)
Slot 1 ("Quad Reverb SM57"), emptied by an earlier buggy run, was restored first
(`--restore 0 "presets/Quad Reverb SM57.pst" "Quad Reverb SM57"` → OK), then reorder validated:
```
CONNECTED  name='AMP Station'  ver=2.5.1  arch=ESP32S3  license=stompstation1
Compatibility: Tested  writesAllowed=True
moving idx 0 ('Quad Reverb SM57') -> idx 2, then back...
   [1/6] slot 1: content ... [6/6] slot 3: name
  OK: 'Quad Reverb SM57' now at idx 2
  OK: order restored to original (round trip 75372 ms)
RESULT: REORDER-TEST PASS
```
Reorder works on hardware: atomic temp-name shuffle, read-back verify, and move-back restores the
original order exactly. Round trip ~75 s for a 3-slot move (UI shows per-slot progress).

### Earlier bug (fixed) — kept for the record
A first run emptied slot 1 because GUID temp names (~43 chars) exceeded the device's ~31-char name
limit, so save-by-name couldn't match. Fixed in commit `e012021` (short `__sstmp_<slot>` names +
reserved-prefix collision guard). The `AggregateException` correctly surfaced both the forward and
rollback failures rather than masking them.

## Lesson folded into the code
- **Device name field caps at ~31 chars.** Reorder temp names must stay short — `__sstmp_<slot>`
  (~10 chars), guarded by a pre-flight check that refuses to reorder if any real preset already
  uses the `__sstmp_` prefix. (A GUID-suffixed name was too long and broke save-by-name on hardware.)
- `WritePresetToSlotAsync`'s read-back verify caught the bad write; the `AggregateException`
  surfaced both the forward and rollback failures rather than masking them.
