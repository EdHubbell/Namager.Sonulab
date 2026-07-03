# NAM → vxamp distiller

Sub-project 2. Converts a source `.nam` capture into a `.vxamp` slot the
Sonulab StompStation pedal can play back natively, by fitting the device's
Wiener–Hammerstein FIR-cascade model to the NAM's impulse and driven response.

Companion references: [`docs/vxamp-format.md`](vxamp-format.md) (the container
format; the output of sub-project 1), [`tools/distiller/FINDINGS.md`](../tools/distiller/FINDINGS.md)
(the authoritative evidence record for every task).

---

## 1. How to run

### Distill a single `.nam`

```
python tools/distiller/distill.py <model.nam> [out.vxamp]
```

`out.vxamp` defaults to `<stem>.vxamp` in the current directory.  Writes a
12288-byte valid `.vxamp` slot and prints the per-amp fidelity row.

### Batch fidelity report (paired corpus only)

```
python tools/distiller/distill.py
```

Prints the device reference loudness, a per-pair fidelity table, and the
total wins vs VoidX.  Run from the repo root (needs `NAMFiles/` in scope).

### Run tests

```
python -m pytest tools/distiller tools/vxamp-re -v
```

All 50 tests should be green; the distiller does not require the pedal.

---

## 2. Pipeline

```
.nam file
   |
   v
[NAM runner]  nam_runner.py
   load_nam_model() — pure-numpy WaveNet / SlimmableContainer forward,
   no torch dependency. Handles prewarm + DC removal.
   |
   v
[Prober]  probe.py
   linear_ir_of_model() — small-signal impulse response (4096–8192 taps)
   at the model's native rate (corpus: 48 000 Hz).
   logmag_corr() — Pearson correlation of log-magnitude spectra; used
   for spectral shape scoring.
   |
   v  resample to device rate (44 100 Hz) via scipy.signal.resample_poly
   |  if model.sample_rate != 44100
   v
[WH fitter]  fit.py
   fit_wh(model) — fits the device tensor dict:
     pre_fir(1024) + g2_fir(1024) + nlmix(1) + fixed headers.
   Two-stage:
   1. Linear cascade split (delta vs min-phase; best L2 wins).
   2. nlmix grid-search [0, 0.7] on 0.3-RMS driven response,
      gain-matched per candidate so the score measures waveshape.
   |
   v
[Loudness normalizer]  distill.py
   loudness_normalize() — simulates our tensors on a fixed 0.3-RMS
   reference signal and folds the dB-matching gain into g2_fir so the
   distilled amp lands at the device reference loudness (+13.53 dBFS).
   |
   v
[Packer]  tools/vxamp-re/nam_to_vxamp.py
   write_vxamp(tensors) — encodes tensors into the 12288-byte container
   (32-byte header + XOR-keystream-obfuscated body + zero padding).
   |
   v
.vxamp file  (valid, playback-ready)
```

---

## 3. Confirmed device model facts

### 3.1 Sample rate

**Device sample rate: 44 100 Hz.**  NAMs in the wild are natively 48 000 Hz;
the distiller resamples the probed impulse response (and the driven probe
signal) to 44 100 Hz before fitting.  Skipping the resample would
frequency-warp the distilled amp by ~9%.

Source: hardware format (`wav_44100` IR list type) confirmed by sub-project 1;
spectral cascade correlations hold at 44 100 Hz across all 14 paired amps
(median 0.947, min 0.733).

### 3.2 Model architecture — Wiener–Hammerstein FIR-cascade

The pedal does **not** run a WaveNet.  VoidX-Control fits every NAM (regardless
of source architecture) into a fixed compact DSP model before upload:

```
y = g2_fir (*) nl(pre_fir (*) x)
```

| Tensor | Taps / elements | Role |
|--------|-----------------|------|
| `pre_fir` | 1024 float32 | Short tone-shaping FIR (~delta; >96% energy in first ~50 taps) |
| `g2_fir` | 1024 float32 | Cab/speaker IR; decaying envelope; carries the spectral shape |
| `nlmix` | 1 float32 | Nonlinear-mix scalar (0 = clean/linear, up to ~0.67) |

Two fixed TLV chunk headers (`g2_header`, `nlmix_header`, 3+4 float32 each)
are byte-identical across all 20 corpus models; the distiller copies them
verbatim from any corpus slot.

See `docs/vxamp-format.md` section 4 for the full body layout and TLV chain.

### 3.3 Loudness reference

**Device reference: +13.53 dBFS output RMS** on a fixed 0.3-RMS Gaussian noise
drive (seed 0, 16 000 samples).  Calibrated as the median of VoidX's 14 paired
corpus cascades simulated on that reference (std 3.3 dB, range +10.0..+21.7 dBFS).

Without this normalization, a NAM-faithful fit sits ~34 dB below stock corpus
amps on the pedal (the source NAMs run at −27..−14 dBFS on the same drive).
The gain is folded into `g2_fir`; the waveshape is untouched.

Cross-check: the VoidX level is **not** derived from the NAM's `metadata.loudness`
field (correlation 0.05 across pairs) — the corpus median is the best available
reference and is recomputed lazily at runtime by `device_reference_db()`.

---

## 4. Nonlinearity — PROVISIONAL

**The one-parameter form is pinned; the exact waveshaper shape is not.**

### Pinned form (`nonlinearity.apply_nl`)

```
r      = rms(pre_fir (*) x)          # mid-signal drive scale
nl(u)  = (1 − s)·u + s · r · tanh(u/r)   # s = nlmix scalar
```

At `s == 0` this is exactly `u` (7 clean corpus models rely on this;
it is bit-for-bit exact).  As `s` increases it blends in a drive-normalized
soft-clip that compresses peaks and adds harmonics.  The pinned form beats
the linear-only path for all 11 driven corpus amps on the shape-error gate.

### Why it is provisional

The corpus cannot pin the waveshaper shape:

- **Rate and level mismatch:** the paired NAMs run at 48 kHz while the device
  runs at 44 100 Hz, and VoidX's cascade carries a ~34 dB gross level offset
  vs the NAM.  After removing a best-fit scalar gain the nonlinearity's
  improvement drops to ~0%, so the corpus, compared at the sample level,
  mainly rewards peak compression and cannot discriminate the true waveshaper
  shape.
- **Even vs odd ambiguity:** VoidX's fitter DLL (`native_add.dll`) names the
  nonlinear term "squareMix" (suggesting even-harmonic content), but a symmetric
  `tanh` (odd) is what robustly passes the raw-error gate.  The true device shape
  is likely an asymmetric soft-clip with both even and odd character; the corpus
  cannot distinguish this.
- **Drive scaling:** the `nlmix_header` bytes carry no per-amp drive/bias/asymmetry
  (they are fixed TLV chunk metadata, identical across all 20 corpus models).
  The RMS normalization in `apply_nl` is a pragmatic stand-in for the device's
  true (unknown) drive normalization.

### To pin the exact form — controlled captures

The fastest path is to send a small set of synthetic `.nam` models through
VoidX-Control, capture the resulting `.vxamp` slots, and compare `nlmix` values:

1. **Pure linear** (identity tap): confirms `nlmix == 0` and a flat response
   yields the identity path.
2. **Symmetric soft-clip drive sweep** (`y = tanh(g·x)/g`, several values of
   `g`): maps `nlmix` vs known drive; reveals whether the shape is `tanh`.
3. **Asymmetric clipper** (e.g. `y = x + a·x²`): tests whether VoidX raises
   `nlmix` for even-harmonic content.
4. **Same IR at two levels:** separates drive normalization from the waveshaper.

This requires a synthetic NAM generator (not yet implemented) and access to a
machine running VoidX-Control.  See `tools/distiller/FINDINGS.md` Task 4 for
the detailed procedure.

---

## 5. Fidelity results

### 5.1 Metric

```
err = 0.5 × [(1 − logmag_corr(linear IRs)) + aligned NRMSE(0.3-RMS driven output)]
```

- **Spectral term:** Pearson correlation of log-magnitude rFFT spectra of the
  small-signal IRs (equivalent to a frequency sweep for the linear path).
- **Driven term:** NRMSE after best-lag alignment (±128 samples, ~±2.9 ms) and
  signed least-squares gain matching.  The signed gain absorbs both level and
  polarity inversion; the lag absorbs bulk time offset.  Both are applied
  identically to our output and VoidX's — the metric measures waveshape, not
  perceptually irrelevant bulk delay or polarity.
- **Corpus:** 14 paired clean / edge-of-breakup amps (SlimmableContainer `.nam`
  files with a matching `.vxamp` slot dumped from the pedal).

### 5.2 Batch result (14 pairs, corrected metric)

| Amp | vx nlmix | our spec_err | vx spec_err | our nrmse | vx nrmse | our_err | voidx_err | ratio |
|-----|--------:|------------:|------------:|----------:|----------:|--------:|----------:|------:|
| Bassman 5F6A - Super Clean | 0.42 | 0.0000 | 0.0254 | 0.0912 | 0.2882 | **0.0456** | 0.1568 | 3.44 |
| Princeton EOB 5 M160 | 0.25 | 0.0184 | 0.0318 | 0.5926 | 0.6417 | **0.3055** | 0.3368 | 1.10 |
| Blackface Twin Reverb 65 2x12 | 0.21 | 0.0034 | 0.0455 | 0.2313 | 0.3027 | **0.1174** | 0.1741 | 1.48 |
| Deluxe Reverb Clean Full | 0.40 | 0.0002 | 0.0261 | 0.7677 | 0.8015 | **0.3840** | 0.4138 | 1.08 |
| Dumble Steel SS Clean Full | 0.25 | 0.0227 | 0.0205 | 0.2615 | 0.5058 | **0.1421** | 0.2632 | 1.85 |
| Dumble Steel SS Drive Full | 0.20 | 0.0200 | 0.0385 | 0.4923 | 0.6289 | **0.2561** | 0.3337 | 1.30 |
| Pano-Verb | 0.00 | 0.0007 | 0.0254 | 0.4834 | 0.6338 | **0.2420** | 0.3296 | 1.36 |
| Princeton Clean 3 SM57 | 0.00 | 0.0035 | 0.0075 | 0.5513 | 0.6385 | **0.2774** | 0.3230 | 1.16 |
| Quad Reverb Randall Head SM57 | 0.30 | 0.0426 | 0.3554 | 0.9970 | 0.7940 | **0.5198** | 0.5747 | 1.11 |
| Roland JC-120 Jazz Chorus | 0.67 | 0.0039 | 0.0279 | 0.5540 | 0.6842 | **0.2789** | 0.3560 | 1.28 |
| Super Reverb EQ Flat SM 57 | 0.23 | 0.0011 | 0.0200 | 0.3034 | 0.3323 | **0.1522** | 0.1762 | 1.16 |
| Twin Reverb SM57 | 0.29 | 0.0103 | 0.0568 | 0.4291 | 0.4001 | **0.2197** | 0.2285 | 1.04 |
| Vibrolux Reverb | 0.00 | 0.0072 | 0.0301 | 0.4989 | 0.7366 | **0.2531** | 0.3834 | 1.51 |
| Vox AC30 Clean | 0.01 | 0.0072 | 0.0199 | 0.4822 | 0.6841 | **0.2447** | 0.3520 | 1.44 |

### 5.3 Honest reading

`our_err <= voidx_err` on all 14/14 nominally, but:

- **10/14 are clear wins** (ratio >= 1.16, median ~1.3x).
- **4 are near-ties** within metric noise: Twin Reverb SM57 (1.04x), Deluxe
  Reverb (1.08x), Princeton EOB (1.10x), Quad Reverb (1.11x).
- **Spectral shape (spec_err):** we win on 13/14 (Dumble Steel SS Clean is the
  exception: 0.0227 vs VoidX 0.0205); margins are mostly 0.01–0.04 absolute.
- **Time-domain driven NRMSE:** we win on 12/14 (lose Twin Reverb SM57
  0.429 vs 0.400 and the known-nonlinear Quad Reverb outlier 0.997 vs 0.794).

The claim to make: our distilled fit **matches VoidX at least as well, and
usually better**, on this clean / edge-of-breakup corpus.  Do not claim a
uniform ~3x margin — only Bassman shows that.  The near-tie rows are metric-
sensitive (a different reasonable metric could flip them).

### 5.4 Metric caveats

- The ±128-sample lag search on a near-decorrelated pair (Quad Reverb,
  best |corr| ≈ 0.08) searches 257 noise correlations.  Absolute Quad numbers
  are soft, though both candidates are scored by the same procedure.
- Perceptually weighted spectra or multi-level scoring could shift the near-tie
  rows.  Human A/B at the pedal is the ground truth.

---

## 6. RE finding — VoidX polarity inversion and bulk time offset

Discovered while correcting the fidelity metric; measured via corpus simulator only.

**Polarity inversion:** VoidX's linear cascade IR has the opposite sign to the
source NAM's IR on **7/14** pairs — Dumble Clean, Dumble Drive, Pano-Verb,
Roland JC-120, Twin Reverb SM57, Vibrolux, Vox AC30.  The signed driven-output
gain is negative on 5/14 of those.

**Bulk time offset:** VoidX's driven output leads the NAM by **1–87 samples
(~0.02–2 ms)** on 13/14 pairs (best lag +2..+87 in the simulator; Vibrolux is −1).
This is consistent with VoidX's near-delta min-phase `pre_fir` compacting latency
that the NAM buries in its recurrent architecture.  Our delta-split fit preserves
the NAM's timing (best lag 0±1 on 13/14).

Both differences are inaudible in isolation.  They matter for **on-device A/B**:
blending a stock VoidX conversion with a distilled amp will phase-cancel / comb on
~half the amps, and any wet/dry mix through a VoidX conversion flips polarity on
those 7 amps.

---

## 7. v1 scope limits

**Supported:** clean to edge-of-breakup amps (NAMs with `nlmix` up to ~0.4 in the
corpus; light overdrive through a natural cab).

**Not supported / known ceiling:** high-gain / heavy distortion.  The container's
one-parameter `nlmix` nonlinearity cannot express the rich even+odd harmonic
content and asymmetric clipping of a high-gain stage.  Any NAM in that regime will
distill to a low-distortion approximation — audibly wrong at moderate drive.

This is a device architecture ceiling, not a distiller bug.  The Quad Reverb Randall
Head SM57 (`nlmix = 0.30`) is the edge case in the current corpus; it has unusually
strong nonlinearity even at low amplitudes (small-signal linearity ratio 0.33 at
1e-3 amplitude vs <0.075 for all other amps) and scores the lowest fidelity (both
ours and VoidX's).

---

## 8. On-device upload and ear-check

### 8.1 Upload a distilled amp

```
dotnet run --project tools/HwCheck -- --upload-amp <file.vxamp> <slotIndex>
```

`slotIndex` is the zero-based amp slot number (0–29).  Use an empty slot or one you
are willing to overwrite.

What the command does (guarded, safe to run):

1. **Backs up** the current contents of that slot to `docs/backups/amp-<slot>-<timestamp>.vxamp`.
2. Writes the name (chunk 0, ASCII, <=31 chars derived from the filename stem).
3. Writes the 12 288-byte payload as 96 chunks of 128 bytes each (chunks 1..96).
4. Sends a terminator chunk (chunk −1) to signal end-of-write to the device.
5. **Reads back** the slot and confirms byte-equality; prints `UPLOAD-AMP OK` or
   `UPLOAD-AMP FAIL — readback mismatch`.

Requires VoidX-Control to be **closed** (it holds COM6 exclusively).

### 8.2 Ear-check procedure

1. Upload the distilled `.vxamp` to a free slot.
2. On the pedal, select that amp slot.
3. Play through the pedal and compare to the source NAM in your DAW (or a
   reference recording) at the same drive level.
4. For a side-by-side comparison against the VoidX-converted version, switch amp
   slots at the pedal.  Bear in mind the polarity inversion and ~2 ms time offset
   that VoidX introduces on some amps (section 6 above) — those differences
   affect A/B blending but not isolated playback.

---

## 9. Deferred / follow-on work

### 9.1 Nonlinearity controlled captures (blocks exact nlmix matching)

The nonlinearity shape is currently modelled as a symmetric `tanh`-based soft-clip
(section 4 above).  To pin the exact form:

- Build a **synthetic NAM generator** that can produce `.nam` files with a known,
  analytic response (linear, `tanh` drive sweep, asymmetric clipper, same IR at
  two levels).
- Push those through VoidX-Control and capture the resulting `.vxamp` slots.
- Compare `nlmix` values and, if available, the `DBG_squareMix.txt` /
  `DBG_levelsDistortionPwr.txt` debug exports the VoidX fitter writes.

Until this is done, the fitted `nlmix` is self-consistent (matches the NAM's own
level-dependent waveshape) but will not reproduce VoidX's values; per-amp drive
character on the pedal may differ from stock VoidX conversions.

### 9.2 Sub-project 2b — Amps-tab UI

A future UI tab in the app (`src/Sonulab.App`) should expose:

- Listing the 30 amp slots (names, occupied/empty).
- Distilling a `.nam` and uploading the result with backup+verify (using the same
  `--upload-amp` logic, wired through the app's device session).
- Browsing / downloading the current slot content.

This is out of scope for sub-project 2 (CLI-only, distiller validated on corpus).
