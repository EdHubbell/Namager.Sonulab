# Plan 3b — Manual Hardware Validation (guarded reorder) — DEFERRED

VoidX-Control CLOSED. Each shifted slot replays ~157 params (~12 s), so a small move takes a minute+.

## STATUS: deferred (operator away from PC, 2026-06-16)

### OUTSTANDING DEVICE REPAIR (do this first when back at the PC)
A reorder-test run on 2026-06-16 hit a bug (temp names exceeded the device's ~31-char name limit)
and **emptied slot 1 (idx 0), "Quad Reverb SM57"**. The bug is fixed (short temp names + collision
guard, commit `e012021`), but the slot still needs restoring. With VoidX CLOSED:

```
dotnet run --project tools/HwCheck -- --restore 0 "presets/Quad Reverb SM57.pst" "Quad Reverb SM57"
```
Expect: `OK: idx 0 now 'Quad Reverb SM57'`. (Short name → writes cleanly.) Then confirm 15/30 presets.

### Then run the guarded reorder validation
```
dotnet run --project tools/HwCheck -- --reorder-test
```
Expect:
1. Connect + Compatibility=Tested, writesAllowed=true.
2. Progress lines per slot ("content"/"name").
3. "now at idx T": the moved preset is at the target slot.
4. "order restored to original": after moving back, names match the original list exactly.

Record pass/fail and the round-trip time. If order isn't restored, capture output and STOP.

## Lesson folded into the code
- **Device name field caps at ~31 chars.** Reorder temp names must stay short — `__sstmp_<slot>`
  (~10 chars), guarded by a pre-flight check that refuses to reorder if any real preset already
  uses the `__sstmp_` prefix. (A GUID-suffixed name was too long and broke save-by-name on hardware.)
- `WritePresetToSlotAsync`'s read-back verify caught the bad write; the `AggregateException`
  surfaced both the forward and rollback failures rather than masking them.
