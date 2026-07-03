# vxamp RE — running findings

## Task 1 — container invariants (confirmed)
- 20 slots, each 12288 B; payload 8256 B; size field = 0x2040.
- 32-B header constant across all models: `4020...c4be`.
- Body = bytes 32..8255 (8224 B).
- Clean source pairs available: 14 (from `pairs()`).

## Task 2 — constant-vs-varying byte map

Report output (`python tools/vxamp-re/analyze_layout.py`):

```
body 8224 B: constant offsets=92 varying=8132
constant islands (offset,len): [(4032, 76), (8204, 16)]
```

- **92 constant bytes** (identical across all 20 models), **8132 varying** — weights dominate.
- Only **2 constant islands**, no regular stride:
  - `(4032, 76)`: 76-byte structural block at roughly the midpoint of the body (body is 8224 B; 4032 is just before the halfway mark). Likely a section header, padding, or inter-block metadata.
  - `(8204, 16)`: 16-byte block at the very end of the body (last 20 bytes). Likely a footer or trailing tag.
- No interleaved per-block scale pattern — the constant offsets cluster into just two contiguous runs rather than appearing at a regular stride, which suggests scale factors (if any) are embedded within the varying weight bytes or encoded differently.
