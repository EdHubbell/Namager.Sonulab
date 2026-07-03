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

## Task 3 — element encoding + scale placement (DISCOVERY)

**Conclusion: `ENCODING = "int8"`, `SCALE_SPEC = "global-constant"`, `SCALE_DEFAULT = 1/127`.**
Body = 8224 bytes → **8224 int8 elements** (record for Task 4 device-arch matching).

### Primary evidence — even/odd byte-distribution discriminator (int16 killer)
Under int16-LE, odd offsets are high bytes and would pile up near 0x00/0xFF (sign extension
of small magnitudes) while even offsets stay ~uniform; under int8 both parities are identical.
Pooled over all 20 bodies (82240 bytes per parity):

```
even: n=82240 mean=127.63 std=73.86 frac near 0x00/0xFF=0.0702
odd : n=82240 mean=129.91 std=74.14 frac near 0x00/0xFF=0.0893
max |CDF_even - CDF_odd| = 0.0234
```

Even and odd distributions are statistically indistinguishable (means 127.6 vs 129.9, stds
73.9 vs 74.1, near-0x00/FF fractions 0.070 vs 0.089, max CDF gap 0.023). No high-byte pile-up.
**→ int16-LE is decisively ruled out.**

### Secondary evidence — int8 histogram / clip counts (body 0)
```
int8 : min -128 max 127 unique 256
int16: min -32730 max 32754 unique 3954
int8 clip counts: [-128]=30 [127]=38     (0.4% / 0.5% of 8224 — negligible)
coarse histogram (16 bins of width 16): [483,574,428,549,454,561,452,597,447,568,427,575,479,567,463,600]
```
Full-range, smooth, nearly flat interior with negligible clipping at ±127 → consistent with a
fixed global scale that does **not** clip. No heavy ±127 spike → no aggressive clipping scale.

### Scale placement — no per-block/interleaved scales
```
stride   16: mean|slot0|=64.9 vs overall=64.3
stride   32: mean|slot0|=63.9 vs overall=64.3
stride   64: mean|slot0|=64.2 vs overall=64.3
stride  128: mean|slot0|=67.4 vs overall=64.3
stride  256: mean|slot0|=64.4 vs overall=64.3
```
No stride shows elevated-magnitude "scale slots"; Task 2 found only 2 contiguous constant
islands (no periodic stride). **→ no interleaved/per-tensor scale table in the body.** The
scale is a single global constant applied to all elements (`SCALE_SPEC = "global-constant"`).

### Ruled-out alternative — float32-LE
A period-4 structure exists (body offset ≡ 3 mod 4 has lower byte-entropy ~6.7–7.2 vs ~7.96
for offsets 0/1/2; differencing two bodies drops pos-3 entropy to ~4.6 with peaks at
0x00/0x80/0xff). This hints at a 4-byte element, but reading the body as float32-LE yields a
**bimodal garbage** distribution (~50% of values ≈ ±1e38, some NaN/inf) — not weight-like.
So the mod-4 pattern is a tensor channel-layout artifact within the int8 stream, not float32.

### Caveat / follow-up
- The exact global **scale magnitude** cannot be fixed statically; `1/127` is a default that
  maps int8 into [-1.008, 1.008]. Refining the true scalar needs a **Task 3E controlled
  single-weight capture** (deferred — user is away from the pedal). The encoding decision
  (int8, global scale) itself is not blocked; only the precise scalar value is pending.
- Element count 8224 (int8) is the figure Task 4 must reconcile against a device WaveNet arch.
  Note the paired NAM weight counts (13802 for WaveNet; 1871+12146 for SlimmableContainer) do
  **not** equal 8224/4112/2056, so the vxamp body is a device-specific re-quantized model, not
  a 1:1 store of the NAM float weights.
