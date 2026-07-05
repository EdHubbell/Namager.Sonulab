# Performance findings — baseline (2026-07-04, fw 2.5.1, COM6)

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
| per-row arrow (MoveStepAsync lean) | 1522 (4 runs: 1604 / 1522 / 1498 / 1507) |

(after-fix table added by the final task)
