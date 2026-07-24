# Spec — dswap reorder engine + targeted usage-map maintenance (Core, Cycle 1)

**Date:** 2026-07-24
**Status:** Design approved (Ed, 2026-07-24). Ready for writing-plans.
**Source:** `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md` items **#1** (dswap reorder
engine) + **#3** (targeted usage-map updates), which that handoff explicitly says to *build
together*.

## Scope & sequencing

The full agreed effort is: rebuild reorder on the atomic firmware `dswap` verb, maintain the
preset-usage map with cheap in-memory transforms instead of a full rescan, **and** deliver amp/IR
reorder. That is delivered as **two sequenced spec→plan→implement cycles**:

- **Cycle 1 (this spec) — Core.** Probe → `dswap` primitive → rewritten `ReorderService` →
  targeted usage-map transforms → rewire `PresetListViewModel`. Fully unit-testable in
  `Sonulab.Core` + `Namager.App` with an extended `FakePresetDevice`. Ships and is hardware-
  validated on its own.
- **Cycle 2 (later, its own brainstorm) — Amp/IR reorder UI.** Drag + button reorder in the amp
  and IR tabs, on top of the Cycle-1 engine (which is written block-agnostic and whose amp/IR
  paths are probed here). Out of scope for this spec.

**Non-goals for Cycle 1:** amp/IR reorder UI; optimizing duplicate or param-edit invalidation
(they keep today's full `Invalidate()` — single-slot `Invalidate(index)` targeted rescan is a
noted follow-up, per handoff #3/#4); paced-overlap pipelining (#2); dwrite byte-exact restore (#5);
`dmove` (unprobed, out of scope).

## Background (verified against the code, 2026-07-24)

- `dswap <path>:{"index":A,"index2":B}\0` atomically swaps two slots — **name AND content,
  firmware-byte-verified, ~213 ms** (PROTOCOL.md "Commands", confirmed live fw 2.5.1, serial,
  `--dswap-probe`). ACK echoes the command.
- Today `ReorderService` (`src/Sonulab.Core/Services/ReorderService.cs`, ~325 lines) has no atomic
  swap, so it emulates one with select+save copies through a temp slot, unique `__sstmp_` temp
  names, a 6-phase adjacent-swap state machine, phase-aware rollback, an empty-neighbor "relocate"
  path, a `rangeHasEmpty` special case, and a param-replay fallback for a full device. All of that
  exists **only because select+save cannot swap atomically**.
- `PresetUsageMap` (`src/Sonulab.Core/Services/PresetUsageMap.cs`) is already immutable and pure,
  built via `Build(occupiedPresets)`; it keys **amp/IR file name → ordered `PresetRef(Index,
  Name)` list**. References are matched by node PATH (`root\app\amp\amp`, `root\app\ir\…\ir`).
- `PresetUsageService.Invalidate()` (`src/Namager.App/Services/PresetUsageService.cs`) clears
  `IsComplete` and triggers a full background rescan (~15–30 s). `PresetListViewModel.RunAsync`
  (line 39) calls it after **every** successful mutation — including a mere reorder, which does not
  change which amps/IRs are referenced.

## Architecture — five components (bottom-up)

### C1. `dswap` transport primitive (block-agnostic)

Add an atomic swap to the block-command layer, path-parameterized so the same primitive serves
`root\app\preset` (Cycle 1) and `root\amp` / `root\ir` (Cycle 2):

- Emit `dswap <path>:{"index":A,"index2":B}\0`, await the ACK, treat a non-echo/timeout as failure.
- Surface on `DeviceRepository` as `SwapPresetSlotsAsync(int a, int b, ct)` for Cycle 1. The
  underlying command builder (SonuCommands) gets the generic `path` form so Cycle 2 reuses it for
  amp/IR with no protocol change.
- **Never** send a non-numeric index (firmware `abort()` → ESP32 reboot — PROTOCOL.md hazard). A/B
  are validated ints in `[0, slotCount)`.

### C2. Rewritten `ReorderService` (dswap-based)

**Full replacement** — the temp-name / phase / relocate / rangeHasEmpty / param-replay machinery is
**deleted**. New behavior:

- **Move = bubble via adjacent swaps.** Moving slot `f`→`t` is `|f−t|` adjacent `dswap`s
  (`swap(f,f∓1)`, … toward `t`). Each swap moves name+content atomically. This reproduces the
  existing `SlotPlanner.Move` semantics (remove-at-`f`, insert-at-`t`) exactly — **verified by
  hand for ranges that contain interior empty slots**: an empty slot bubbles like any element, so
  the net effect is the correct rotation. The `rangeHasEmpty` special case and the empty-neighbor
  "relocate" path are therefore unnecessary.
- **Single-step move** (`MoveStepAsync`, the common up/down UI case) = exactly **one** `dswap`
  (~213 ms vs ~1.5 s today), whether the neighbor is occupied or empty.
- **No temp slot** → the device-full fallback disappears. **No save-by-name** → the name-uniqueness
  precondition and the `__sstmp_` reserved-prefix guard are removed.
- **Verify + rollback:** after each swap, read-back-verify the two slot **names** (as today's lean
  paths do — name is the device's authoritative slot identity, and content moved atomically under
  firmware byte-verify). On mismatch, issue the **reverse `dswap`** to restore and abort with a
  clear error. No param-replay fallback.
- **Cancellation:** a move is a sequence of individually-complete swaps; cancelling between swaps
  leaves a **valid intermediate order** (no torn multi-phase copy window like today). Verify-and-
  stop on cancel.
- **Active-slot handling:** determined by the probe (C0 below). If `dswap` disturbs the live
  preset, the engine records the currently-selected preset before the move and re-selects it after;
  if not, no special handling. The design accommodates either verdict; the plan's first task
  resolves it.

`MoveAsync(from,to)` and `MoveStepAsync(from,up)` keep their existing public signatures and
`IProgress<ReorderProgress>` contract (one report per completed swap).

### C3. `PresetUsageMap` pure transforms

Add immutable transforms (each returns a new map; existing `Build` unchanged):

- `WithMovedSlot(int from, int to)` — apply the net rotation of `[min(from,to), max(from,to)]` to
  every `PresetRef.Index` in every ref list (names ride along with content under `dswap`), then
  re-sort each list ascending by index. This is the map-side mirror of the engine's bubble move,
  applied **once** for the whole move.
- `WithRenamedPreset(int index, string newName)` — set `Name = newName` on every `PresetRef` whose
  `Index == index`.
- `WithoutSlot(int index)` — remove every `PresetRef` whose `Index == index` from every list (drop
  now-empty keys).

### C4. `IPresetUsageService` targeted notifications

Add three notifications that transform `Current` in place, **keep `IsComplete` true**, and raise
`MapUpdated`:

- `NotifyPresetMoved(int from, int to)` → `Current = Current.WithMovedSlot(from, to)`
- `NotifyPresetRenamed(int index, string newName)` → `Current.WithRenamedPreset(...)`
- `NotifyPresetDeleted(int index)` → `Current.WithoutSlot(index)`

Each is a no-op on `NullPresetUsageService`. They must be safe to call from the UI thread after a
verified device mutation. If `IsComplete` was already false (a rescan is mid-flight), the targeted
transform still applies to the partial `Current` and leaves `IsComplete` as-is (do not force it
true mid-scan) — correctness is preserved because the in-flight scan will re-derive from the device.

### C5. `PresetListViewModel` rewiring

On **verified success only**:

- Reorder (`MoveStepAsync`/`MoveAsync`) success → `NotifyPresetMoved(from, to)` **once** instead of
  `Invalidate()`.
- Rename success → `NotifyPresetRenamed(index, newName)`.
- Delete success → `NotifyPresetDeleted(index)`.
- **Duplicate** and **param-edits** → keep `Invalidate()` (full rescan) unchanged in Cycle 1.
- On failure / rollback → keep `Invalidate()` as the safe fallback (the reorder path read-back-
  verifies; a failure means the on-device state is uncertain, so a rescan is correct).

Result: a reorder/rename/delete no longer triggers a ~15–30 s rescan, and the rescan highlight
flicker (handoff #4) is eliminated for those three cases.

## C0. Probe (first plan task — gates the engine build)

Extend `tools/HwCheck --dswap-probe`:

- **`--path <root\app\preset|root\amp|root\ir>`** so one probe covers all three blocks.
- **Active-slot test:** select a preset (make it live), `dswap` its slot with another, then check
  whether the live/working preset changed. **Records the verdict that decides C2's active-slot
  handling.**
- **Amp/IR test:** `dswap` two amp slots and two IR slots; byte-verify both moved (name+content);
  confirm no crash/reboot.

Ed runs it on-device; the verdict is written into `PROTOCOL.md` (amp/IR dswap support + active-slot
behavior). Engine implementation follows the recorded verdict.

## Error handling

- Swap ACK missing/timeout, or post-swap name verify mismatch → reverse-swap to restore, abort with
  a descriptive error; VM falls back to `Invalidate()`.
- Index out of range / non-numeric → rejected before any device I/O (crash hazard).
- Rollback failure → surface an `AggregateException` ("reorder failed and rollback also failed;
  device may be inconsistent") consistent with today's contract.

## Testing strategy

- **`FakePresetDevice`** gains a faithful atomic `dswap` (swap name+content of two slots; reject
  non-numeric/out-of-range indices). This lets the entire engine be exercised offline.
- **Engine tests:** single-step move (occupied + empty neighbor) = one swap; multi-slot bubble
  move matches `SlotPlanner.Move` output including interior-empty ranges; verify-failure →
  reverse-swap rollback; cancellation leaves a valid order; boundary no-ops.
- **Pure-map tests:** `WithMovedSlot` / `WithRenamedPreset` / `WithoutSlot` over representative
  ref layouts, including re-sort and empty-key drop; net-rotation parity with a full rebuild.
- **Service tests:** each `Notify…` transforms `Current`, keeps `IsComplete` true, raises
  `MapUpdated`; `NullPresetUsageService` no-ops.
- **VM tests:** reorder/rename/delete success calls the targeted notify (not `Invalidate`);
  duplicate + failure paths still call `Invalidate`.
- Full suite stays green (currently 648).

## Hardware validation (after merge, before relying on it)

- Reorder single-step and multi-slot moves on-device; confirm order + names + content correct and
  fast (~213 ms/step).
- Confirm the active-slot verdict from C0 holds in the real engine (live preset intact, or
  correctly re-selected).
- Confirm reorder/rename/delete no longer trigger a full usage rescan (highlights update instantly,
  no flicker) and remain correct.
- Add to / update the relevant `docs/HARDWARE-VALIDATION-*.md` checklist.

## Key references

- `docs/superpowers/2026-07-24-post-scan-fix-next-steps.md` — items #1, #3 (and #4 flicker).
- `PROTOCOL.md` — `dswap`/`dmove` verbs, dread hazards (non-numeric → reboot).
- `src/Sonulab.Core/Services/ReorderService.cs` — the machinery being replaced.
- `src/Sonulab.Core/Services/PresetUsageMap.cs` — immutable map + `Build`.
- `src/Namager.App/Services/PresetUsageService.cs` — `Invalidate()` + scan lane.
- `src/Namager.App/ViewModels/PresetListViewModel.cs` — `RunAsync` line 39 mutation hook.
- `tools/HwCheck` — `--dswap-probe` (to be extended with `--path` + active-slot test).
