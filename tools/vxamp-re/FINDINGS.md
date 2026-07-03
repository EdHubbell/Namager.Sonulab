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

## Task 3 — element encoding (DISCOVERY, corrected after review)

**Conclusion (fix pass): the body is `float32-LE` weights obfuscated with a repeating
byte keystream.** De-obfuscate, then read 4-byte little-endian floats:

```
k[i] = (K0[i % 32] - 0x20 * (i // 32)) mod 256          # K0 = KEYSTREAM_BASE (32 bytes)
weights = float32_LE( body[i] XOR k[i] )                # 2056 elements
```

- **`ELEMENT_DTYPE = "float32-le"`**, **2056 elements**, **no quantization scale** (float weights).
- `OBFUSCATION = ("xor-keystream", 32, -0x20)`.
- The legacy `ENCODING = "int8"` / `SCALE_SPEC` / `SCALE_DEFAULT = 1/127` constants are kept only
  because the accepted vocabulary is `{int8,int16,int8+block-scale}` and **cannot name float32**;
  they are **PROVISIONAL / SUPERSEDED** by `ELEMENT_DTYPE`. Task 4 must try **both** element counts
  recorded in `ELEMENT_COUNTS`: **2056 (float32-le, favoured)** and 8224 (int8, legacy).

### Why the original "int8 + global scale" was wrong (review counter-evidence, reproduced)
Linearly-quantized NN weights under a global scale are strongly **zero-peaked**. The raw int8
view is instead **flat/uniform** at every phase (offset mod 4) — evidence *against* int8:

```
per-phase int8 (pooled 20 bodies; uniform expectation mean|int8|=64, std=73.9, frac|v|<=16=0.129):
  phase 0: entropy=7.961 mean|int8|=64.0 std=73.9 frac|v|<=16=0.126
  phase 1: entropy=7.994 mean|int8|=63.6 std=73.6 frac|v|<=16=0.133
  phase 2: entropy=7.992 mean|int8|=64.1 std=74.0 frac|v|<=16=0.130
  phase 3: entropy=7.160 mean|int8|=64.2 std=74.1 frac|v|<=16=0.123
```
Phase 3 alone is low-entropy. Cross-body XOR entropy per phase collapses **only at phase 3**:
`[7.98, 7.98, 7.98, 4.11]` → a structured 4th byte (float sign+exponent) shared across models,
bytes 0-2 high-entropy (mantissa) → a **4-byte record**. This is the exponent byte; it is not
magnitude-elevated, so the earlier "high-magnitude scale-slot" stride scan could never see it.

### The keystream + how it was recovered
The constant island at body `(4032,76)` (Task 2) is **float32 0.0 padding**: it is byte-identical
across all 20 models, so there its ciphertext *is* the keystream. From it the 32-byte base and the
`-0x20`-per-32-byte ramp were recovered (`k[i+32] = k[i] - 0x20`, verified across the island). The
recovered keystream:
- reproduces the padding island (first 64 of 76 bytes; the 12-byte tail is shared metadata), and
- de-obfuscates that region to **exactly float32 0.0 for every body**.

### Acceptance test — decoded values are zero-peaked & bounded (unlike int8)
Pooled over all 20 bodies, and per body, contrasting the candidate decodes:
```
RAW float32-LE (no deobf) : frac|<64=0.517 std=6.09  kurt=  66  -> garbage (50% ~±1e38, NaN/inf)
int8 (raw)                : frac|<64=0.498 std=36.9  kurt=  -1  -> FLAT (uniform, not weight-like)
XOR-keystream float32-LE  : frac|<64=1.000 std=0.49  kurt=3824  -> ZERO-PEAKED (weight-like) ✓
```
All 20 bodies individually: `frac|<64 = 1.000`, per-body std 0.08–1.47, excess kurtosis 100–1560
(heavy zero peak). For calibration the real NAM source weights are also zero-peaked (std 0.117,
kurt 67). The single non-weight float per body (idx 2053) sits inside the trailer metadata island
`(8204,16)` — a length field + ASCII tag, not a weight; `weights()` masks both islands.

### int16-LE — ruled out (reworded per review)
```
even: n=82240 mean=127.63 std=73.86 frac near 0x00/0xFF=0.0702
odd : n=82240 mean=129.91 std=74.14 frac near 0x00/0xFF=0.0893
max |CDF_even - CDF_odd| = 0.0234
```
There is **no int16-LE high-byte signature** (odd offsets do not pile up near 0x00/0xFF as int16
sign-extension would require) → **int16-LE ruled out**. (This is *not* claimed as statistical
indistinguishability: the 0.0234 CDF gap is ~3.5× the ~0.0067 significance threshold and is real —
it is the phase-3 keystream/exponent structure leaking into the odd parity, consistent with the
float32-under-keystream model above.)

### Follow-up
- **No quant scale to determine** under float32, so the Task 3E controlled single-weight capture is
  **no longer needed to fix the encoding**. (A capture could still independently confirm the
  keystream / element order and resolve the exact tensor layout for Task 4.)
- The paired NAM source weight counts (13802 WaveNet; 1871+12146 SlimmableContainer) still do not
  equal 2056, so the 2056-float body is a **device-specific re-derived model** (smaller WaveNet),
  not a 1:1 copy of the NAM float weights. Reconciling 2056 floats to a device arch is Task 4.
