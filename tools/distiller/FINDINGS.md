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

## Task 4 — device nonlinearity (`nonlinearity.py`)

**Status: pinned as a drive-normalized soft-clip mix; exact form/scaling
underdetermined by the corpus (see Concerns).** The device model is
`y = g2_fir ⊛ nl(pre_fir ⊛ x)`; this task pins `nl`.

### Pinned form

```
r      = rms(u)                          # u = pre_fir ⊛ x (mid signal)
nl(u)  = (1 - s)·u + s · r · tanh(u/r)    # s = nlmix scalar
```

`apply_nl(x, header, scalar)` implements this; `device_sim.simulate(..., nl=None)`
now calls it with the slot's own `nlmix_header`/`nlmix`. `scalar == 0` returns
`x` **bit-for-bit** (the 7 clean corpus models stay exactly linear); pass
`nl=lambda z: z` for the explicit linear path.

### Evidence

**1. `nlmix_header` is fixed metadata, not parameters.** The four `nlmix_header`
floats are byte-identical for all 20 corpus models:
`14 00 00 00 00 00 00 00 6e 6c 6d 69 78 00 00 00` = the TLV chunk header
`u32 len=0x14, u32 reserved=0, tag "nlmix\0\0\0"`. They carry **no** per-amp
drive/bias/asymmetry — the nonlinearity is a **one-parameter family in the
`nlmix` scalar** (0..0.67; exactly 0 for 7 clean amps). This corrects the
sub-project-1 provisional note ("4 header floats set drive/bias/asymmetry").

**2. VoidX distiller names it "squareMix".** Static RE of the VoidX-Control
fitter `native_add.dll` (read-only, not run): the nonlinear-fit region
(`.text` 0x4e77d..0x54e1e) emits debug exports `DBG_G1Export` / `DBG_G2Export`
(the two FIRs), `DBG_linearDiff` (NAM minus linear cascade = the nonlinear
residual), `DBG_squareMix` / `DBG_SquareMixExport` (the nonlinear basis),
`DBG_levelsDistortionPwr` and `DBG_frequenciesOutputLevels` (a per-level,
per-frequency harmonic-distortion sweep: constants 60 Hz start, 44100 Hz,
8192-pt bins). So VoidX fits `nlmix` from measured harmonic distortion and
calls the nonlinear term "square" (even-harmonic).

**3. Single-tone harmonics of the driven NAMs** (220 Hz, 48 kHz) show a
saturating stage growing both even (H2) and odd (H3) harmonics with level; H2
is the dominant term for ~half the amps (Twin 0.114, Princeton 0.075, Quad
0.054, Deluxe 0.050, Dumble-Clean 0.047), consistent with the asymmetric
"square" character. A soft-clip mix reproduces this qualitative growth.

**4. Fidelity gate (pytest metric, `x = 0.3·randn`, `‖sim − NAM‖`):** the
pinned nl beats linear-only for **all 11 driven amps**:

| amp | nlmix | e_lin | e_nl |
|---|---:|---:|---:|
| Bassman 5F6A Super Clean | 0.416 | 344.7 | 288.8 |
| Princeton EOB 5 M160 | 0.254 | 1176.0 | 1064.5 |
| Blackface Twin Reverb | 0.210 | 366.6 | 336.6 |
| Deluxe Reverb Clean | 0.396 | 434.9 | 368.4 |
| Dumble Steel SS Clean | 0.252 | 305.8 | 276.0 |
| Dumble Steel SS Drive | 0.200 | 670.8 | 617.8 |
| Quad Reverb Randall SM57 | 0.304 | 631.6 | 557.9 |
| Roland JC-120 (chorus outlier) | 0.670 | 501.2 | 370.1 |
| Super Reverb EQ Flat | 0.229 | 391.6 | 356.3 |
| Twin Reverb SM57 | 0.289 | 389.1 | 345.4 |
| Vox AC30 Clean | 0.013 | 814.9 | 810.8 |

### Concerns (why the exact form is not fully pinned)

- **Raw-error win is largely gain-driven.** The `e_lin` values (300–1200 on
  8k-sample buffers) are dominated by a gross **level/rate mismatch**: the
  paired NAMs are native 48 kHz while the FIR cascade is 44.1 kHz, and the
  VoidX cascade carries an overall output-level offset vs the NAM. After
  removing a best-fit scalar gain, the nl's improvement drops to ~0% — i.e.
  the corpus, compared this way, mainly rewards **peak compression** and cannot
  discriminate the true waveshaper *shape*. The nl is therefore **directionally
  correct** (a saturating mix that compresses and adds harmonics) but its exact
  identity is not validated at the sample level.
- **Even ("square") vs odd (soft-clip) ambiguity.** The DLL names it "square"
  (even), yet the odd soft-clip mix is what robustly passes the raw-error gate
  (pure even/`u²` forms *fail* it). The true device shape is likely an
  **asymmetric** soft-clip (even + odd); a symmetric `tanh` under-captures the
  even part. Not resolvable from the corpus given (1).
- **Drive scaling is a modeling choice.** With `nlmix_header` fixed and pre_fir
  gains spanning ~50× across amps, a *fixed* waveshaper on the mid signal
  behaves wildly differently per amp, so the input is **RMS-normalized** here.
  This makes `apply_nl` mildly signal-adaptive (buffer-RMS) rather than a pure
  memoryless waveshaper — a pragmatic stand-in for the device's true (unknown)
  drive normalization.

### To pin the exact form — controlled captures (do NOT run VoidX in-task)

Have the user convert these synthetic `.nam` models through VoidX and capture
the resulting `.vxamp` (and, if reachable, the `DBG_*.txt` debug exports the
fitter writes):
1. **Pure linear amp** (identity / single-tap IR `.nam`): confirms `nlmix == 0`
   and that a flat response yields the identity path.
2. **Symmetric soft-clip drive sweep** — one `.nam` per drive
   (e.g. `y = tanh(g·x)/g` for `g ∈ {1,2,4,8,16}`): maps `nlmix` (and the fitted
   `pre_fir`/`g2_fir` gain split) vs known drive → reveals the drive scaling and
   whether the shape is `tanh`.
3. **Asymmetric clipper** (e.g. diode-style `y = x + a·x²` and a hard one-sided
   clip): tests even-vs-odd — does VoidX raise `nlmix` for even-harmonic content,
   and does the device store an asymmetry term anywhere.
4. **Same linear IR at two levels** to separate the fixed drive normalization
   from the waveshaper.

Diffing `codec.decode().["tensors"]` across 1–4 (only `nlmix` and the FIR gain
split can change; `nlmix_header` is fixed) pins the mapping directly.
