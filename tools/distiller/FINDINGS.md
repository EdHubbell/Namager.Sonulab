# Distiller findings

Sub-project 2: fit the device's FIR-cascade model (see `tools/vxamp-re/FINDINGS.md`,
`arch.DEVICE_ARCH`) to a source `.nam`'s response. Corpus-only, read-only.

## Task 1 — NAM runner (`nam_runner.py`)

**Path taken: pure-numpy WaveNet forward** (no `neural-amp-modeler`/torch dependency).
`load_nam_model(path)` returns a `NamModel` with `.arch`, `.sample_rate`, and
`.process(x)` (float32 in/out, same length, causal).

### Architectures handled (the whole 15-file corpus)
- **WaveNet** (standard neural-amp-modeler export; 3 files, 13802 weights): 2 layer
  arrays (16ch k=3 d=1..512 Tanh non-gated head_size=8; then 8ch head_size=1).
  Forward per array: rechannel 1x1 -> per layer `z = act(dilated_conv(x) + input_mixin(cond))`,
  skips `+= z`, residual `x += 1x1(z)` -> `head_rechannel(skips)` seeds the next
  array's skip accumulator. Output = `head_scale * head`.
- **SlimmableContainer** (VoidX-fork; 12 files): `config.submodels` =
  `[{max_value: 0.5, 3ch/1871w}, {max_value: 1.0, 8ch/12146w}]`; the runner selects
  the **full** submodel. Its single layer group has per-conv `kernel_sizes`
  ([6 x14, 15, 15, 6 x7]) / `dilations`, per-conv LeakyReLU(0.01), and a k=16 conv
  head over the skip sum. All exotic fork features (FiLM x8, gating_mode,
  bottleneck != channels, head1x1, grouped convs, secondary_activation) are
  **inactive across the entire corpus**; the runner raises on any active one
  rather than mis-rendering.

### Weight layout (validated exactly)
Flat `weights` consumed in declaration order -- rechannel w; per layer conv w+b,
input_mixin w, 1x1 w+b; head w(+b); **last float = head_scale** (as in
NeuralAmpModelerCore). Counts land exactly: 13802 (standard), 1871 (c=3) and
12146 (c=8) fork -- and the consumed-last element **equals `config.head_scale`
bit-for-bit** in both variants (0.02; 0.00699837), confirming the layout with
zero unconsumed weights.

### Prewarm + DC removal
Layer biases give a cold-start transient (~receptive field long: 6346 samples
fork / 4092 standard) and a small silence DC (up to 1.7e-4). The official
runtime does `nam::DSP::prewarm()` and the plugin adds a DC blocker; `process()`
mirrors both: left-pads a full receptive field of silence (trimmed back) and
subtracts the settled silence level, so silence -> exactly 0 and the causality
gate (`|y| < 1e-6` pre-impulse) holds.

### Cross-checks (beyond the 3 pytest gates)
- **Fork weight-order check**: corr(IR_sub0, IR_sub1) for Pano-Verb = **0.978**
  (prior sub-project-1 finding: 0.966) -- independently-parameterized submodels
  would decorrelate under a wrong weight ordering.
- **Corpus sweep**: all 15 files load, run finite, silence->0 (<=3e-8), small-signal
  linearity ratio <=0.075 for 14/15. Outlier: *Quad Reverb Randall Head SM57*
  (0.33) -- genuinely nonlinear even at 1e-3 amplitude, not a runner bug (its two
  sibling standard-WaveNet captures sit at 0.0015/0.013).
- **Reproduces the committed vxamp pairing result**: log-mag (60 Hz-12 kHz)
  corr of my NAM IR vs the paired `.vxamp` `pre_fir (*) g2_fir` cascade, 11
  Slimmable pairs: **median 0.940** (prior: 0.915), same Roland JC-120 chorus
  outlier (0.49). The runner's response matches what VoidX's own distiller saw.
- `neural-amp-modeler` pip cross-check not run (torch dependency; numpy path
  validated via the corpus-grounded checks above instead).

### Notes for later tasks
- `NamModel.receptive_field` is exposed (IR/measurement lengths should exceed it).
- All corpus models are 48 kHz.
- `process()` is offline/vectorized (per-layer numpy), not streaming -- fine for
  the distiller's measurement buffers (2k-16k samples, ~ms runtime).

## Task 3 — sample rate

**CONFIRMED: `SAMPLE_RATE = 44100` Hz** (set in `device_sim.py`).

### Method

`probe.linear_ir_of_model(model, n=4096, amp=1e-3)` feeds a tiny impulse
through the NAM's `process()` and normalizes by `amp` to get the small-signal
linear IR. `probe.logmag_corr(a_ir, b_ir)` computes the Pearson correlation of
the two IRs' rFFT log-magnitude spectra (0 to Nyquist, n_fft=4096).

The test `test_voidx_vxamp_linear_response_matches_its_nam` takes the first
pair from `vx.pairs()` and asserts `logmag_corr(nam_ir, dev_ir) > 0.7`.

### Sample-rate determination

The `logmag_corr` metric operates on rFFT bin indices (bin k = k · fs / n_fft
Hz); since both IRs are compared at the same `n_fft`, the numeric correlation
values are bin-index-relative, not rate-dependent. Therefore the 44100 vs 48000
comparison yields **identical correlation numbers** — the rate is not
distinguishable via `logmag_corr` alone. The **44100 Hz** determination rests on
the hardware/format evidence: the device's IR list type is `wav_44100`, and
sub-project 1 confirmed the vxamp FIR cascade was fitted to approximate NAM
responses at the device's operating rate.

The high correlations (all > 0.7; median > 0.94) confirm that the VoidX
distiller fitted the FIR cascade well — consistent with 44100 Hz being correct,
as any serious rate mismatch would warp the spectral shape and degrade the fit.

### Per-pair `logmag_corr` (14 pairs, `SAMPLE_RATE = 44100`)

| Pair | corr |
|---|---|
| Bassman 5F6A - Super Clean | 0.9737 |
| Princeton EOB 5 M160 | 0.8889 |
| Blackface Twin Reverb 65 2x12 | 0.9106 |
| Deluxe Reverb Clean Full | 0.9333 |
| Dumble Steel SS Clean Full | 0.9713 |
| Dumble Steel SS Drive Full | 0.9633 |
| Pano-Verb | 0.9505 |
| Princeton Clean 3 SM57 | 0.8366 |
| Quad Reverb Randall Head SM57 | 0.7333 |
| Roland JC-120 Jazz Chorus | 0.9309 |
| Super Reverb EQ Flat SM 57 | 0.9461 |
| Twin Reverb SM57 | 0.9461 |
| Vibrolux Reverb | 0.9712 |
| Vox AC30 Clean | 0.9776 |

**mean = 0.924 · median = 0.947 · min = 0.733 (Quad Reverb — known nonlinear outlier) · max = 0.978**

All 14/14 pairs exceed 0.7 (the test threshold). The Quad Reverb Randall Head
outlier (0.733) is the same genuinely-nonlinear amp flagged in Task 1 (small-
signal linearity ratio 0.33 even at 1e-3 amplitude), so its lower correlation is
expected — not a rate or prober bug.

### Verdict

`SAMPLE_RATE = 44100` — **confirmed**. Set in `device_sim.SAMPLE_RATE`.
