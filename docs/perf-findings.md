# Performance findings (baseline 2026-07-04, after-run 2026-07-06; fw 2.5.1)

## Connect phases (before fixes)
| phase | ms |
|---|---|
| open+settle | 1511 |
| probes (1 attempt) | 20 |
| compat | 419 |
| presets-list | 66 |
| amps-list | 90 |
| irs-list | 77 |
| **total to fully loaded** | **2183** |

## Reorder single step (before fixes)
| path | ms |
|---|---|
| toolbar (MoveAsync: backup + rotate) | 8718 (backup 7312ms + path 1406ms) |
| per-row arrow (MoveStepAsync lean) | 1533 avg (4 runs: 1604 / 1522 / 1498 / 1507) |

## Connect phases (after fixes)

After-run conditions: TRUE cold boot — pedal USB unplugged 5 s and replugged before Connect
(the baseline connect was a warm reconnect, attempts=1). The replug re-enumerated the CH340 on
COM8 (baseline: COM6); port auto-discovery found it without a pinned port.

| phase | before ms | after ms |
|---|---|---|
| open+settle | 1511 | 258 |
| probes | 20 (1 attempt) | 2069 (3 attempts — cold boot; warm runs 19–50 ms, 1 attempt) |
| compat | 419 | 392 |
| presets-list | 66 | 61 |
| amps-list | 90 | (lazy — first tab visit: 86) |
| irs-list | 77 | (lazy — first tab visit: 77) |
| **total to usable (presets shown)** | 2016 (2183 fully loaded) | 2780 cold boot / 724 warm |

The warm "total to usable" (724 ms = 255 + 19 + 370 + 80) is a full measured connect cycle from
the Task 4 hardware check (2026-07-04 23:33, attempts=1) — the like-for-like comparison against
the warm baseline: **2016 ms → 724 ms (−64%)**. The cold-boot 2780 ms is the worst case: the
ESP32 was still rebooting after the port open, the first two probes were eaten, and the adaptive
loop recovered on attempt 3.

## Reorder single step (after)
| path | before ms | after ms |
|---|---|---|
| toolbar | 8718 (MoveAsync: backup 7312 + rotate 1406) | 1676 (MoveStepAsync — lean, zero dreads) |
| per-row arrow | 1533 avg (4 runs: 1604 / 1522 / 1498 / 1507) | 1453 avg (3 runs: 1506 / 1540 / 1312) (unchanged path) |

No `MoveAsync` lines appeared in the after-run log — the toolbar buttons are confirmed on the
lean content-free path. Toolbar single-step move: **8718 ms → 1676 ms (−81%)**.

## Preset-dwrite verdict

WORKS. The `--preset-dwrite-probe` HwCheck run (2026-07-04, fw 2.5.1, serial) proved preset
content IS dwrite-able with the correct sequence — chunk:0 name, chunks 1..64 content, name at
chunk:-1 as the commit; the 2026-06-15 "not dwrite-able" finding failed only because it used the
zeros terminator. Measured: 10251 ms for the 66-ACKed-write upload + byte-equality verify, vs
~216 ms for a select+save copy and ~12 s for a param replay. So select+save remains the reorder
copy engine (~50x faster), and dwrite becomes the byte-exact option for restore/duplicate
(follow-up, not built). Full verdict text lives in PROTOCOL.md ("Reorder / copy / backup"
section, VERDICT 2026-07-04).

## Conclusions

Connect got ~3x faster warm (2016 → 724 ms to presets shown, −64%) by replacing most of the fixed
1500 ms open-settle with a 250 ms settle + adaptive probe polling, and by making the Amps/IRs
list reads lazy (each ~80 ms, now paid on first tab visit instead of at connect). The toolbar
move is ~5x faster (8718 → 1676 ms, −81%) now that it uses the same content-free MoveStepAsync
swap as the per-row arrows (~1.3–1.7 s per step, unchanged — that residual is the device-side
select+save pace, not app overhead).

Cold-boot validation (2026-07-06): with the pedal USB-unplugged 5 s and replugged before Connect,
the connect still succeeded — the ESP32 was genuinely mid-boot, probes took 2069 ms with
attempts=3, and the adaptive loop absorbed it (total 2780 ms, well inside the ~3.9 s worst-case
budget). No OpenSettleMs bump needed. Two surprises worth remembering: the USB replug
re-enumerated the pedal on a different COM port (COM6 → COM8), which the port auto-discovery
handled transparently; and the preset-dwrite probe overturned a "CRITICAL" 2026-06-15 finding —
preset content was always dwrite-able, the old test just used the wrong terminator.
