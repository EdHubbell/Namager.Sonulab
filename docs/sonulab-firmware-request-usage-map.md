# Firmware request: a cheap preset→amp/IR usage read

Audience: Sonulab firmware (AMP Station / StompStation, ESP32-S3). Scope: two additive protocol
proposals, no host-side code attached. Current firmware version referenced throughout: 2.5.1.

## 1. Problem

A preset selects an amp model and up to two IR files **by name** (`root\app\amp\amp`,
`root\app\ir\ir`, `root\app\ir\ir2\ir` — see `PROTOCOL.md` "Renumber / rename"). Renaming or
deleting an amp/IR silently orphans every preset still pointing at the old name; VoidX does this
without warning. A host that wants to warn the user first needs to know, for every occupied
preset slot, which amp and IR(s) it references — but that mapping is not exposed anywhere except
inside each preset's own 8192-byte document.

The wire protocol has no batch read: `dread` returns exactly one 128-byte chunk per round trip,
and extra numeric args (`count`, `size`, …) are silently ignored (`PROTOCOL.md` "dread limits and
hazards"). Lockstep round-trip cost is ~57 ms/chunk; a paced-overlap send (≥30 ms between sends)
sustains ~33 ms/chunk (~1.75x), but there is no way to ask for more than one chunk per command.
The three reference nodes are not clustered at the front of the document — measured on real
device captures, the amp ref lands around chunk 7, the primary IR around chunk 11, and the
secondary IR around chunk 23 (of 64 total, 8192 B / 128 B per preset). A head-read stopping as
soon as all three ref lines are complete needs roughly 14–25 chunks per preset in practice.

Scanning every occupied slot (up to 30) to build the full map therefore costs, at minimum,
14–25 round trips x 30 presets, i.e. roughly 15–30 s per connect even with the trimmed head-read
and paced sends — before any amp/IR list can be shown with usage highlights, and before a
rename/delete guard can trust its answer. There is no firmware verb that returns this mapping, or
even a subset of it, in fewer than one round trip per chunk per preset.

## 2. Request A (preferred): a preset-refs read

A single command that returns, per occupied preset slot, the three name values the firmware
already parses out of the document at preset load (amp select, primary IR select, secondary IR
select) — the same three values the device itself must read to activate a preset. For example:

```
read root\presets\refs
```

returning one record per occupied slot:

```json
[
  {"index": 0, "preset": "Lead", "amp": "Plexi", "ir": "Cab A", "ir2": ""},
  {"index": 3, "preset": "Clean", "amp": "Fender Twin", "ir": "Cab B", "ir2": "Cab C"}
]
```

(empty slots omitted, or included with blank fields — either is fine; a host filters on the
existing `root\presets` name list either way). Even served as ~30 individual CRLF records — one
per occupied slot, matching the existing `path:{json}` framing (`PROTOCOL.md` "Wire framing") —
this is at worst one round trip per occupied preset instead of 14–25, i.e. roughly 1–2 s total for
a full bank versus 15–30 s: a 10–20x improvement, and comparable to how `read root\presets`
already returns the 30-name list as one node today.

This fits the existing self-describing node convention: `browse <path>` already returns a schema
object per node (`desc`/`type`/`min`/`max`/`options`/`ref`/…, `PROTOCOL.md` "CONFIRMED via live
read-only probe"), so `root\presets\refs` would simply be one more browsable node, discoverable
the same way a host already discovers `root\presets`/`root\amp`/`root\ir`.

## 3. Request B (alternative, cheaper to build): a change counter

If a full refs read is too much firmware work, the cheapest useful primitive is a monotonically
increasing counter that the firmware already has enough information to bump:

```
root\presets\_rev   (u32, incremented on any preset save / rename / delete / dswap)
```

A host can then cache its own scan result indefinitely and only re-scan when this value changes,
rather than re-scanning on every connect. This requires no new parsing of preset content —
just one counter bump alongside operations the firmware already performs (`write …,"save":"save"`,
`dwrite …chunk:-1`, `dswap`, per `PROTOCOL.md` "Reorder / copy / backup" and "Renumber /
rename"). A per-slot version, e.g. `"rev":[<30 ints>]` reported alongside the existing
`root\presets` name list, would be strictly better: a host could re-scan only the slots that
actually changed instead of the whole bank, at the same implementation cost as the single global
counter (one integer per slot vs. one integer total).

## 4. What we do today, without either of these

NAMager reads a windowed head of each preset (`DeviceRepository.ReadPresetHeadAsync`, up to
`HeadChunkCap` = 32 chunks, stopping early once all three ref lines are seen or a content-end NUL
is hit) and runs the scan in the background so it never blocks the UI
(`src/Namager.App/Services/PresetUsageService.cs`). To make even that scan feel instant on
reconnect, results are persisted to a small per-device disk cache keyed on `root\sys\_id`
(`src/Namager.App/Services/PresetUsageCache.cs`) and used to seed provisional highlights at zero
device reads, which the background scan then revalidates and overwrites row-by-row. This works,
but it is a workaround for the missing read, not a substitute: an in-place edit made directly on
the pedal (outside the app) is undetectable until the background scan reaches that slot again, so
a provisional highlight can be stale for up to one full scan pass (~15–30 s). Delete/rename guards
never trust the cache — they wait for a scan to actually complete.

## 5. Compatibility note

Both requests are purely additive (new nodes; no change to `read`/`browse`/`write`/`dread`/`dwrite`
semantics), so they are invisible to VoidX and any other existing host. NAMager feature-detects
new nodes via `browse` before using them, so nothing breaks talking to older firmware that lacks
`root\presets\refs` or `root\presets\_rev` — the host simply falls back to the current head-read
scan.
