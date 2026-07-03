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

## Task 4 — device architecture + tensor layout (DISCOVERY, PROVISIONAL roles / EXACT boundaries)

**Conclusion: the device does NOT store (or run) a WaveNet.** The 2056 floats are a compact
**FIR-cascade (Wiener–Hammerstein-style) DSP model** — pre-filter → nonlinear stage → post-filter —
that VoidX-Control *re-derives* from the source `.nam`. `DEVICE_ARCH` in `arch.py` records this with
`"wavenet": None`; `status: PROVISIONAL` applies to the section *roles/topology*, not the boundaries.

### Tensor table (`tensor_sizes()`, sums to 2056 = `ELEMENT_COUNTS["float32-le"]`)

| name           | floats | body bytes    | role                                                        |
|----------------|-------:|---------------|-------------------------------------------------------------|
| `pre_fir`      | 1024   | [0, 4096)     | short tone-shaping FIR; taps 1008..1023 = 0.0 in all corpus |
| `g2_header`    | 3      | [4096, 4108)  | TLV: `u32 len=0x100C, u32 0, tag "G2\0\0"`                  |
| `g2_fir`       | 1024   | [4108, 8204)  | long post FIR (cab/speaker IR); `[0]` may be a drive scalar |
| `nlmix_header` | 4      | [8204, 8220)  | TLV: `u32 len=0x14, u32 0, tag "nlmix\0\0\0"`               |
| `nlmix`        | 1      | [8220, 8224)  | nonlinear-mix scalar; 0..0.67 in corpus, exactly 0 for 7    |

Reconciliation: 1024 + 3 + 1024 + 4 + 1 = **2056** ✓. The body is a chained TLV container:
`raw 4096 B` + chunk `"G2"` (len 0x100C) + chunk `"nlmix"` (len 0x14) = 4096+4108+20 = 8224 B,
verified to land exactly on the body end for **all 20 models** (`arch.parse_chunks`). Task 2's
"constant islands" are exactly the pre_fir zero tail + the two constant chunk headers.

### Evidence the sections are FIR curves, not NN weights
- Lag-1 autocorrelation: `g2_fir` = 0.32..0.95, `pre_fir` up to 0.85 across bodies — smooth curves.
  Real NAM weight vectors give 0.16 (iid-like). NN-weight readings are dead.
- `pre_fir` is near-delta: ~100 % of its energy sits in the first ~50 taps (peak at tap 0, e.g.
  1.12, 52.3, 6.3 …) → short EQ/tone filter. `g2_fir` decays over hundreds of taps → cab/speaker IR.
- Candidate WaveNet param counts can't hit the section sizes: the VoidX-fork formula (validated
  exactly: c=3 → 1871, c=8 → 12146 vs the Pano-Verb submodels) has no config near 1008/1024 that
  also explains two independent smooth sections.

### Gold(ish) validation — cascade matches each pair's linear response
A numpy re-implementation of the VoidX-fork WaveNet forward (rechannel → per layer
[dilated conv+bias, +input-mixin, LeakyReLU, residual 1×1] with Σz → head conv k=16 → ×head_scale;
submodel self-consistency corr(IR_sub0, IR_sub1)=0.966) gives each source model's small-signal
impulse response. Log-magnitude spectra (60 Hz–12 kHz) vs the body sections, 11 Slimmable pairs:

```
median corr: g2_fir alone 0.845 | pre_fir alone 0.734 | pre_fir ⊛ g2_fir 0.915  ← cascade wins
```
Only outlier: Roland JC-120 (0.35) — a chorus amp (time-varying), expected to fit poorly.
So the two FIRs are in **series** and jointly carry the model's linear response; `nlmix` (=0 for
several clean models) gates a nonlinear stage between them ("G2" plausibly = gain/stage-2 params).

### Provisional / what a controlled capture would settle (Task 3E / sub-project 2)
- The runtime topology (where the nonlinearity sits, its shape, what exactly `G2`'s scalar-vs-tap
  first payload float is). Convert one `.nam` twice with a single knob changed, or capture a
  known-IR model, and diff `split_body()` outputs.
- Encoder implications (Tasks 5+): building a `.vxamp` no longer means exporting WaveNet weights —
  it means *fitting/deriving* two FIRs + nlmix from the `.nam` (linear IR extraction is already
  reproducible in numpy per the validation above).

## Task 6 — repack-vs-refit verdict (CONCLUDED: REFIT)

**`VERDICT = "refit"`** — the device does NOT store or derive its body from the source `.nam` weights.
VoidX-Control *distills* the source WaveNet/SlimmableContainer into an entirely different model class
(FIR-cascade, Wiener-Hammerstein-style) and the device body is not reproducible from the `.nam`
without re-running VoidX's internal FIR/nonlinearity fitting process.

### Model-class argument

| Layer | Source `.nam` | Device body |
|-------|--------------|-------------|
| Architecture | WaveNet (dilated conv + LeakyReLU + residual) or SlimmableContainer | FIR-cascade: `pre_fir` (1024 taps) + `g2_fir` (1024 taps) + `nlmix` scalar |
| Weight count | 1871 (Slim sub0), 12146 (Slim sub1), or 13802 (WaveNet) | 2056 float32 (2048 filter taps + 8 metadata/scalar) |
| Tensor shapes | No shared dimension with device tensors | Fixed 1024-tap FIR × 2, no conv layers |
| `model_class_match` | **False** across all 14 corpus pairs | — |

There is no mapping from source weight tensors to device tensors: the architectures are incompatible.

### Per-pair weight-space evidence (`verdict.compare()`, all 14 corpus pairs)

`exact_frac` = fraction of source weights appearing verbatim (within 1e-4) as a contiguous run in
the de-obfuscated device float stream (`decode_body.as_float32`).
`corr` = Pearson correlation, device weight vector vs best-size source submodel (aligned length).
`max_abs_err` = max |device − source| on that aligned comparison.

```
Pair                               exact_frac      corr  max_abs_err   source counts
--------------------------------  ----------  --------  -----------   ----------------
Bassman 5F6A - Super Clean          0.005208    0.0194       8.7122   sub0=1871 sub1=12146
Princeton EOB 5 M160                0.000145    0.0174      52.4715   root=13802
Blackface Twin Reverb 65 2x12       0.002996    0.0846       2.6367   sub0=1871 sub1=12146
Deluxe Reverb Clean Full            0.001355   -0.0392       8.4234   sub0=1871 sub1=12146
Dumble Steel SS Clean Full          0.001498    0.2061       2.3754   sub0=1871 sub1=12146
Dumble Steel SS Drive Full          0.001355    0.0539       8.1879   sub0=1871 sub1=12146
Pano-Verb                           0.001355    0.0699       4.0216   sub0=1871 sub1=12146
Princeton Clean 3 SM57              0.000145   -0.0439      17.2159   root=13802
Quad Reverb Randall Head SM57       0.000145    0.0165      25.2847   root=13802
Roland JC-120 Jazz Chorus           0.001641    0.0250       5.5003   sub0=1871 sub1=12146
Super Reverb EQ Flat SM 57          0.001498   -0.0257       4.3444   sub0=1871 sub1=12146
Twin Reverb SM57                    0.001355   -0.0610       3.6351   sub0=1871 sub1=12146
Vibrolux Reverb                     0.001355   -0.1321       8.5109   sub0=1871 sub1=12146
Vox AC30 Clean                      0.000499   -0.0903      24.1161   sub0=1871 sub1=12146

Summary: exact_frac min=0.000145  max=0.005208  all < 0.01 ✓
         corr range −0.13 … +0.21  (noise; no pair exceeds |corr| > 0.21)
         max_abs_err range 2.4 … 52.5  (large across the board)
```

- **`exact_frac` ≤ 0.0052 for every pair** — the residual non-zero values are incidental collisions
  in the near-zero tail (both FIR filters and NN weights have zero-peaked distributions; the
  16-tap zero-padding run in `pre_fir[1008:1024]` can match a zero stretch in source, giving a
  short run, but never more than ~10 values out of ≥1871 source weights, keeping the fraction well
  below 0.01). This is **not a repack**: source weights are absent from the device body.
- **`corr` ≈ 0** — no linear relationship (not a scaling/reordering of source weights).
- **`max_abs_err` ≫ 0** — magnitudes differ by 2–52 across all pairs.

### Spectral distillation (CITED from Task 4, not recomputed here)

```
median log-mag corr: pre_fir ⊛ g2_fir vs WaveNet IR = 0.915   (11 Slimmable pairs)
```

The two FIRs in series jointly fit the source model's linear response — confirming the device body
is a **fitted approximation** (distillation), not a repack of the source weights.

### Conclusion and path forward

Byte-exact `.nam` → `.vxamp` reproduction is **NOT achievable** without reproducing VoidX's internal
FIR/nonlinearity fitting workflow — we cannot assemble a valid `.vxamp` simply by copying or
transforming NAM weight tensors.

**Path forward = (c) sub-project 2: fit our own FIR-cascade.**
The linear IR fitting is already validated in numpy (spectral corr 0.915 on the Task 4 cascade).
The remaining work is:
1. Extract the small-signal IR from the source `.nam` (WaveNet forward, already implemented).
2. Fit `pre_fir` + `g2_fir` to that IR (e.g., least-squares Wiener filter or IFFT).
3. Determine the `nlmix` scalar from the source's nonlinear behaviour (or default 0.0 for clean amps).
4. Assemble using `codec.encode()` (Task 5 encoder, verified byte-exact).
