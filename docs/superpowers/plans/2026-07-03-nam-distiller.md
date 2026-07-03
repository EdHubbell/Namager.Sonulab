# NAM → vxamp Distiller Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert a clean/edge-of-breakup `.nam` into a device `.vxamp` by fitting the device's Wiener–Hammerstein FIR-cascade to the NAM's own response, at fidelity ≥ VoidX's on the same NAM.

**Architecture:** A Python (numpy/scipy) pipeline under `tools/distiller/` that reuses sub-project 1's cracked-format modules (`tools/vxamp-re/`): run the NAM → probe its response → fit `pre_fir` + nonlinearity + `g2_fir` → pack into a `.vxamp` via the existing byte-exact writer. A numpy device-model simulator grades output offline against both the NAM and VoidX's own vxamp. The device nonlinearity form (left provisional by sub-project 1) is pinned first, since everything downstream depends on it.

**Tech Stack:** Python 3.11+, numpy, scipy, pytest. Reuses `tools/vxamp-re/`. Optional oracle: `neural-amp-modeler` (pip) for cross-checking the NAM runner. One C# change to `tools/HwCheck` for a guarded upload.

## Global Constraints

- **Reuse sub-project 1 modules, do not reimplement them.** `tools/vxamp-re/`: `decode_body` (`deobfuscate`, `as_float32`, `weights`, `keystream`, `KEYSTREAM_BASE`), `arch` (`DEVICE_ARCH`, `tensor_sizes`, `split_body`, `parse_chunks`), `codec` (`decode`, `encode`, `roundtrip_ok`), `nam_to_vxamp` (`write_vxamp`), `vxamp` (`load_nam`, `nam_weights`, `pairs`, `corpus_root`, `vxamp_files`).
- **Device container facts (verbatim):** vxamp body = float32-LE XOR-obfuscated; de-obfuscated = 2056 float32 in a TLV FIR-cascade: `pre_fir` (1024 taps) ‖ `g2_header` (3 floats) + `g2_fir` (1024 taps) ‖ `nlmix_header` (4 floats) + `nlmix` (1). Tensor names/sizes come from `arch.tensor_sizes()`; `arch.split_body(body)` yields them as a dict.
- **The device nonlinearity's exact function is PROVISIONAL** until Task 4 pins it. `nlmix` corpus range 0..0.67; **0 on 7 of 20 amps** (fully linear).
- **v1 fidelity scope = clean → edge-of-breakup.** High-gain is a documented stretch goal, not a requirement.
- **Corpus + dataset only; READ-ONLY on device** except the single guarded upload in Task 7 (backs up the target slot first, per repo write-safety rules in `CLAUDE.md`).
- **Discovery tasks (4 = pin nonlinearity; parts of 5 = fit conventions; 6 = fidelity thresholds) are analysis, not pure TDD.** Their "test" asserts an invariant that must hold once the finding is correct, AND the task appends its conclusion + evidence to `tools/distiller/FINDINGS.md`. A reviewer gates on the written finding plus the test.
- **Device sample rate is unconfirmed** (IR list is `wav_44100` → likely 44100 Hz). Confirm empirically in Task 3 before fitting; use a module constant `SAMPLE_RATE` so it changes in one place.
- Run tests from repo root: `python -m pytest tools/distiller tools/vxamp-re -v`.
- **Every git commit message must end with these two trailer lines:**
  ```
  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01GVMhjpcXJV1sbeBZnuv8G2
  ```
- Do NOT commit `__pycache__` (already gitignored). `.superpowers/` is gitignored — never `git add -f` a report there.

## File Structure

- `tools/distiller/conftest.py` — puts `tools/vxamp-re` on `sys.path` so distiller modules can import the sub-project-1 modules.
- `tools/distiller/requirements.txt` — numpy, scipy, pytest.
- `tools/distiller/nam_runner.py` — load + run a `.nam` (numpy WaveNet forward; SlimmableContainer submodel selection).
- `tools/distiller/device_sim.py` — numpy sim of the device forward model `y = g2_fir ⊛ nl(pre_fir ⊛ x)`.
- `tools/distiller/nonlinearity.py` — the pinned firmware nonlinearity (Task 4 output).
- `tools/distiller/probe.py` — test-signal generation + response/nonlinearity measurement.
- `tools/distiller/fit.py` — the Wiener–Hammerstein fitter.
- `tools/distiller/distill.py` — top-level CLI glueing runner→probe→fit→pack.
- `tools/distiller/FINDINGS.md` — running findings (sample rate, nonlinearity, VoidX conventions, fidelity).
- `tools/distiller/test_*.py` — per-module tests.
- `tools/HwCheck/Program.cs` — add guarded `--upload-amp`.
- `docs/distiller.md` — usage + results.

---

### Task 1: Scaffold + NAM runner

**Files:**
- Create: `tools/distiller/conftest.py`, `tools/distiller/requirements.txt`, `tools/distiller/nam_runner.py`, `tools/distiller/test_nam_runner.py`, `tools/distiller/FINDINGS.md`

**Interfaces:**
- Produces:
  - `load_nam_model(path) -> NamModel`
  - `NamModel.process(x: np.ndarray) -> np.ndarray` (float32 in/out, same length; the amp's audio response)
  - `NamModel.arch: str`, `NamModel.sample_rate: int | None` (from the `.nam` if present)

- [ ] **Step 1: Path shim + deps**

Create `tools/distiller/conftest.py`:
```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "vxamp-re"))
```
Create `tools/distiller/requirements.txt`:
```
numpy>=1.26
scipy>=1.11
pytest>=8.0
```
Run: `python -m pip install -r tools/distiller/requirements.txt`

- [ ] **Step 2: Write the failing test**

Create `tools/distiller/test_nam_runner.py`:
```python
import numpy as np
import vxamp as vx
import nam_runner as nr

def test_loads_both_corpus_arches():
    slim = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    assert slim.arch == "SlimmableContainer"
    wave = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Princeton Clean 3 SM57.nam")
    assert wave.arch == "WaveNet"

def test_process_is_finite_same_length_and_causal_quiet():
    m = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    x = np.zeros(2048, dtype=np.float32); x[100] = 1.0   # impulse
    y = m.process(x)
    assert y.shape == x.shape
    assert np.isfinite(y).all()
    assert np.allclose(y[:100], 0.0, atol=1e-6)          # causal: no output before the impulse

def test_small_signal_is_roughly_linear():
    # at tiny amplitude a guitar-amp model is ~linear: doubling input ~doubles output
    m = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Twin Reverb SM57.nam")
    x = (np.random.default_rng(0).standard_normal(4000).astype(np.float32)) * 1e-3
    y1 = m.process(x); y2 = m.process(2 * x)
    num = np.linalg.norm(y2 - 2 * y1); den = np.linalg.norm(2 * y1) + 1e-12
    assert num / den < 0.05
```

- [ ] **Step 3: Run test to verify it fails**

Run: `python -m pytest tools/distiller/test_nam_runner.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'nam_runner'`.

- [ ] **Step 4: Implement the NAM runner**

Create `tools/distiller/nam_runner.py`. Implement the NAM **WaveNet** forward pass in numpy (NAM is open-source; the WaveNet is a stack of dilated causal conv layers with gated `tanh*sigmoid` activations, 1×1 mixers, skip accumulation, and a linear head — the `.nam` `config.layers[*]` gives `input_size`, `condition_size`, `channels`, `kernel_sizes`, `dilations`, `activation`, and `head`; `weights` is a flat float array consumed in declaration order). For `SlimmableContainer`, select the full submodel (`max_value == 1.0`) and run its WaveNet. Reuse `vxamp.nam_weights` only for weight extraction; the forward pass is new here.
- Keep it correct over fast: process the impulse/short buffers the tests use (a few thousand samples) — vectorize per layer, no need for streaming.
- If a numpy forward for `SlimmableContainer` proves intractable in the task budget, report DONE_WITH_CONCERNS and fall back to the `neural-amp-modeler` pip package as the runtime engine (wrap its inference); note the dependency in `requirements.txt`.

(The exact per-layer arithmetic is transcription of the NAM WaveNet definition; implement it to satisfy the causality + small-signal-linearity + finiteness tests, which are the real correctness gates.)

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest tools/distiller/test_nam_runner.py -v`
Expected: PASS (3 tests).

- [ ] **Step 6: Seed FINDINGS + commit**

Create `tools/distiller/FINDINGS.md` with a `## Task 1 — NAM runner` note (which arch path, whether the numpy forward or the pip fallback was used, any cross-check vs `neural-amp-modeler` if installed).
```bash
git add tools/distiller/conftest.py tools/distiller/requirements.txt tools/distiller/nam_runner.py tools/distiller/test_nam_runner.py tools/distiller/FINDINGS.md
git commit -m "distiller: NAM runner (numpy WaveNet forward) + scaffold"
```

---

### Task 2: Device-model simulator (linear path)

**Files:**
- Create: `tools/distiller/device_sim.py`, `tools/distiller/test_device_sim.py`

**Interfaces:**
- Consumes: `arch.split_body`, `codec.decode`, `nam_to_vxamp.write_vxamp`
- Produces:
  - `simulate(tensors: dict, x: np.ndarray, nl=None) -> np.ndarray` — device forward model
    `y = g2_fir ⊛ nl(pre_fir ⊛ x)`. When `nl is None`, the nonlinearity is the identity (linear path),
    which is exactly correct for `nlmix == 0` amps.
  - `linear_ir(tensors) -> np.ndarray` — the cascade's linear impulse response `pre_fir ⊛ g2_fir`
    (used when `nl` is identity).
  - `tensors_from_vxamp(slot_bytes) -> dict` — convenience: `codec.decode(slot)["tensors"]`.

- [ ] **Step 1: Write the failing test**

Create `tools/distiller/test_device_sim.py`:
```python
import numpy as np
import vxamp as vx, codec
import device_sim as ds

def _linear_pair_tensors():
    # a corpus amp with nlmix == 0 (fully linear) — pick by scanning
    for f in vx.vxamp_files():
        t = codec.decode(vx.load_vxamp(f))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-9:
            return t
    raise AssertionError("no linear amp in corpus")

def test_simulate_linear_matches_convolution_of_firs():
    t = _linear_pair_tensors()
    x = np.zeros(4096, dtype=np.float32); x[0] = 1.0     # impulse -> output is the cascade IR
    y = ds.simulate(t, x, nl=None)
    ir = ds.linear_ir(t)
    n = min(len(y), len(ir))
    assert np.allclose(y[:n], ir[:n], atol=1e-5)

def test_simulate_finite_and_same_length():
    t = _linear_pair_tensors()
    x = np.random.default_rng(1).standard_normal(8000).astype(np.float32) * 0.1
    y = ds.simulate(t, x, nl=None)
    assert y.shape == x.shape and np.isfinite(y).all()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/distiller/test_device_sim.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Implement the linear device sim**

Create `tools/distiller/device_sim.py`: `simulate` applies `pre_fir` as an FIR (`scipy.signal.lfilter(pre_fir,1,x)` or `np.convolve(..., mode="full")[:len(x)]`), then `nl` (identity if None), then `g2_fir` as an FIR; return same-length float. `linear_ir` = `np.convolve(pre_fir, g2_fir)`. Handle the header tensors (`g2_header`, `nlmix_header`) as metadata not used in the linear path (document that Task 4 wires them into `nl`).

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest tools/distiller/test_device_sim.py -v`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/distiller/device_sim.py tools/distiller/test_device_sim.py
git commit -m "distiller: device-model simulator (linear path)"
```

---

### Task 3: Response prober + confirm the device sample rate

**Files:**
- Create: `tools/distiller/probe.py`, `tools/distiller/test_probe.py`
- Modify: `tools/distiller/device_sim.py` (add `SAMPLE_RATE` constant), `tools/distiller/FINDINGS.md`

**Interfaces:**
- Consumes: `nam_runner`, `device_sim`, `codec`, `vxamp.pairs`
- Produces:
  - `SAMPLE_RATE: int` (in `device_sim.py`)
  - `linear_ir_of_model(model, n=4096, amp=1e-3) -> np.ndarray` — small-signal impulse response of a NAM
  - `logmag(ir, n_fft=4096) -> np.ndarray` — log-magnitude spectrum
  - `logmag_corr(a_ir, b_ir) -> float` — correlation of two IRs' log-magnitude spectra (fidelity metric)

- [ ] **Step 1: Write the failing test (also the sample-rate confirmation)**

Create `tools/distiller/test_probe.py`:
```python
import numpy as np
import vxamp as vx, codec
import nam_runner as nr, device_sim as ds, probe as pb

def test_voidx_vxamp_linear_response_matches_its_nam():
    # For a paired (nam, VoidX vxamp): the vxamp's linear cascade IR (small-signal) must resemble the
    # NAM's small-signal IR IF the sample rate is right. This both exercises the prober AND confirms rate.
    name, nam_path, vxamp_path = next(iter(vx.pairs()))
    model = nr.load_nam_model(nam_path)
    nam_ir = pb.linear_ir_of_model(model)
    t = codec.decode(vx.load_vxamp(vxamp_path))["tensors"]
    dev_ir = ds.linear_ir(t)
    corr = pb.logmag_corr(nam_ir, dev_ir)
    assert corr > 0.7   # VoidX's own fit correlates strongly with the NAM's linear response

def test_logmag_corr_self_is_one():
    ir = np.random.default_rng(2).standard_normal(2048)
    assert pb.logmag_corr(ir, ir) > 0.999
```

- [ ] **Step 2: Run to verify it fails**

Run: `python -m pytest tools/distiller/test_probe.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Implement prober + set SAMPLE_RATE**

Add `SAMPLE_RATE = 44100` to `device_sim.py` (the leading hypothesis). Create `probe.py` with `linear_ir_of_model` (feed a tiny impulse through `model.process`, return the response), `logmag` (rFFT magnitude in dB), `logmag_corr` (Pearson corr of two dB spectra over a shared band).

- [ ] **Step 4: Run + confirm the rate**

Run: `python -m pytest tools/distiller/test_probe.py -v`
If `test_voidx_vxamp_linear_response_matches_its_nam` PASSES at 44100, the rate is confirmed. If it FAILS, try `SAMPLE_RATE = 48000` and re-run; whichever gives the higher corr across several `vx.pairs()` is the device rate. Record the winner + the corr numbers in `FINDINGS.md` under `## Task 3 — sample rate`.

- [ ] **Step 5: Commit**

```bash
git add tools/distiller/probe.py tools/distiller/test_probe.py tools/distiller/device_sim.py tools/distiller/FINDINGS.md
git commit -m "distiller: response prober + confirmed device sample rate"
```

---

### Task 4: Pin the device nonlinearity (DISCOVERY — highest risk)

**Files:**
- Create: `tools/distiller/nonlinearity.py`, `tools/distiller/test_nonlinearity.py`
- Modify: `tools/distiller/device_sim.py` (wire `nl` default to the pinned function), `tools/distiller/FINDINGS.md`

**Interfaces:**
- Consumes: `nam_runner`, `device_sim`, `probe`, `codec`, `vxamp.pairs`
- Produces:
  - `apply_nl(x: np.ndarray, header: np.ndarray, scalar: float) -> np.ndarray` — the firmware nonlinearity as a function of the `nlmix_header` (4 floats) and `nlmix`.
  - `device_sim.simulate(..., nl=None)` default changed so `None` now means "use `nonlinearity.apply_nl` with the tensors' own header/scalar" (identity still exactly recovered when `scalar == 0`).

- [ ] **Step 1: Investigation driver**

Create an investigation function in `nonlinearity.py` (`_investigate()` runnable via `__main__`) that, for each `vx.pairs()` model with `nlmix > 0`: runs the NAM at increasing input levels, runs the device linear path (`pre_fir⊛x`) at the same levels, and compares the NAM's harmonic/level behavior to isolate the static nonlinearity the device must be applying. Print, per driven amp: `nlmix`, the 4 header floats, and the measured input→output transfer at several levels. Goal: identify the functional form (e.g., `x + scalar·(f(drive·x) − x)` for some fixed waveshaper `f` like `tanh`, with the header floats setting drive/bias/asymmetry).

- [ ] **Step 2: Run the investigation**

Run: `python tools/distiller/nonlinearity.py`
Read the transfer curves. Determine the form of `f` and how `header`+`scalar` parameterize it. If the static-nonlinearity form is not identifiable from the corpus alone, STOP and report BLOCKED recommending controlled captures (user runs synthetic driven NAMs through VoidX) — do NOT run VoidX yourself.

- [ ] **Step 3: Write the failing test**

Create `tools/distiller/test_nonlinearity.py`:
```python
import numpy as np
import vxamp as vx, codec
import nam_runner as nr, device_sim as ds, probe as pb, nonlinearity as nl

def test_nl_is_identity_when_scalar_zero():
    x = np.linspace(-1, 1, 501).astype(np.float64)
    y = nl.apply_nl(x, header=np.zeros(4), scalar=0.0)
    assert np.allclose(y, x, atol=1e-9)

def test_driven_amp_fidelity_improves_with_nl_vs_linear():
    # For a driven corpus amp, the FULL device sim (with nl) must match the NAM better than the
    # linear-only sim — proving the pinned nl captures real behavior.
    for name, nam_path, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-6:
            continue
        model = nr.load_nam_model(nam_path)
        x = (np.random.default_rng(3).standard_normal(8000).astype(np.float32)) * 0.3
        ref = model.process(x)
        y_lin = ds.simulate(t, x, nl=lambda z: z)
        y_nl  = ds.simulate(t, x, nl=None)   # None -> pinned apply_nl
        e_lin = np.linalg.norm(y_lin - ref); e_nl = np.linalg.norm(y_nl - ref)
        assert e_nl <= e_lin + 1e-6
        return
    import pytest; pytest.skip("no driven amp in corpus")
```

- [ ] **Step 4: Implement `apply_nl` + wire into device_sim**

Implement `apply_nl` per the Step-2 finding, ensuring `scalar==0` → identity. Change `device_sim.simulate`'s `nl=None` default to call `nonlinearity.apply_nl(z, tensors["nlmix_header"], tensors["nlmix"])`. Run the tests.

- [ ] **Step 5: Run + record**

Run: `python -m pytest tools/distiller/test_nonlinearity.py -v`
Expected: PASS. Append the pinned nonlinearity form + evidence (transfer curves, per-amp fidelity gain) to `FINDINGS.md` under `## Task 4`. If BLOCKED at Step 2, document the blocker and the exact synthetic NAMs the user should run.

- [ ] **Step 6: Commit**

```bash
git add tools/distiller/nonlinearity.py tools/distiller/test_nonlinearity.py tools/distiller/device_sim.py tools/distiller/FINDINGS.md
git commit -m "distiller: pin device nonlinearity + wire into simulator"
```

---

### Task 5: Wiener–Hammerstein fitter

**Files:**
- Create: `tools/distiller/fit.py`, `tools/distiller/test_fit.py`
- Modify: `tools/distiller/FINDINGS.md`

**Interfaces:**
- Consumes: `nam_runner`, `device_sim`, `probe`, `nonlinearity`, `arch.tensor_sizes`
- Produces:
  - `fit_wh(model) -> dict` — a tensor dict with exactly `arch.tensor_sizes()` names (`pre_fir`, `g2_header`, `g2_fir`, `nlmix_header`, `nlmix`), fitting the NAM's response into the device parameterization.

- [ ] **Step 1: Write the failing test (recover a known device model)**

Create `tools/distiller/test_fit.py`:
```python
import numpy as np
import vxamp as vx, codec, arch
import device_sim as ds, fit as ft

class _SimModel:
    # a fake "NAM" whose response IS a known device model, so the fitter must recover it
    def __init__(self, tensors): self.t = tensors; self.arch = "WaveNet"; self.sample_rate = ds.SAMPLE_RATE
    def process(self, x): return ds.simulate(self.t, x, nl=None)

def test_fit_returns_correct_tensor_shape():
    t = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))["tensors"]
    out = ft.fit_wh(_SimModel(t))
    assert {k: np.asarray(v).size for k, v in out.items()} == {n: c for n, c in arch.tensor_sizes()}

def test_fit_recovers_a_linear_device_model():
    # pick a linear corpus amp; a model that already IS that device model must fit back to ~itself
    for f in vx.vxamp_files():
        t = codec.decode(vx.load_vxamp(f))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-9:
            break
    fitted = ft.fit_wh(_SimModel(t))
    x = np.random.default_rng(4).standard_normal(8000).astype(np.float32) * 0.1
    ref = ds.simulate(t, x, nl=None); got = ds.simulate(fitted, x, nl=None)
    assert np.linalg.norm(got - ref) / (np.linalg.norm(ref) + 1e-9) < 0.05
```

- [ ] **Step 2: Run to verify it fails**

Run: `python -m pytest tools/distiller/test_fit.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Implement the fitter**

Create `fit.py`. Approach: (1) probe the model's small-signal linear response → design the linear cascade; split it into `pre_fir` (1024 taps) and `g2_fir` (1024 taps) per the WH convention calibrated in Task 4/FINDINGS (default: put the minimum-phase/pre-emphasis part in `pre_fir`, the cabinet/coloration in `g2_fir`; if Task 4 didn't pin a split, put the full linear IR in `g2_fir` and a unit impulse in `pre_fir` for linear amps). (2) Fit the nonlinearity params (`nlmix` + header) by matching the model's level-dependent behavior via `nonlinearity.apply_nl` (for linear amps → `scalar=0`, headers zeroed). (3) Assemble the tensor dict with `g2_header`/`nlmix_header` from the fit (or defaults matching the corpus for linear amps). Window/normalize taps to the corpus convention recorded in FINDINGS.

This is a DISCOVERY-flavored task: the exact split + normalization are calibrated to VoidX using `vx.pairs()`. Record the chosen conventions in `FINDINGS.md` under `## Task 5`. The two tests (shape + linear round-trip) are the hard gates.

- [ ] **Step 4: Run to verify it passes**

Run: `python -m pytest tools/distiller/test_fit.py -v`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add tools/distiller/fit.py tools/distiller/test_fit.py tools/distiller/FINDINGS.md
git commit -m "distiller: Wiener-Hammerstein fitter (linear + nonlinearity)"
```

---

### Task 6: End-to-end distill CLI + dataset fidelity (DISCOVERY gate)

**Files:**
- Create: `tools/distiller/distill.py`, `tools/distiller/test_distill.py`
- Modify: `tools/distiller/FINDINGS.md`

**Interfaces:**
- Consumes: `nam_runner`, `fit`, `nam_to_vxamp.write_vxamp`, `device_sim`, `probe`
- Produces:
  - `distill(nam_path) -> bytes` — full 12288-byte `.vxamp` from a `.nam`
  - `fidelity_vs_nam(nam_path) -> dict` — `{"our_err": float, "voidx_err": float | None, "our_vs_voidx": float | None}` where errors are the log-mag+NRMSE metric of the device sim vs the NAM (VoidX side only for paired models)

- [ ] **Step 1: Write the failing test**

Create `tools/distiller/test_distill.py`:
```python
import numpy as np
import vxamp as vx
import distill as dl

def test_distill_produces_valid_uploadable_container():
    out = dl.distill(vx.corpus_root() / "FullCaptures" / "Twin Reverb SM57.nam")
    assert len(out) == vx.SLOT_SIZE
    assert vx.header(out).hex() == vx.HEADER_HEX
    assert vx.size_field(out) == vx.PAYLOAD_SIZE

def test_clean_amp_fidelity_at_least_matches_voidx():
    # On a clean paired amp, our distilled model's error vs the NAM must be <= VoidX's error.
    name = "Twin Reverb SM57"
    r = dl.fidelity_vs_nam(vx.corpus_root() / "FullCaptures" / f"{name}.nam")
    assert r["voidx_err"] is not None
    assert r["our_err"] <= r["voidx_err"] * 1.10   # within 10% of VoidX, target is <=
```

- [ ] **Step 2: Run to verify it fails**

Run: `python -m pytest tools/distiller/test_distill.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Implement the CLI + fidelity harness**

Create `distill.py`: `distill(nam_path)` = `write_vxamp(fit_wh(load_nam_model(nam_path)))`. `fidelity_vs_nam` runs the device sim of our fitted tensors and (for paired models) VoidX's decoded tensors against the NAM's response using the Task-3 metric. Add a `__main__` that distills a `.nam` path arg to a `.vxamp` file and prints the fidelity row. Also add a batch report over `vx.pairs()` printing our_err vs voidx_err per amp.

- [ ] **Step 4: Run + record the clean-subset result**

Run: `python -m pytest tools/distiller/test_distill.py -v` then `python tools/distiller/distill.py` (batch).
Append to `FINDINGS.md` `## Task 6`: the per-amp our_err vs voidx_err table over the clean/edge-of-breakup subset, and confirm the DoD (our_err ≤ voidx_err on a majority). If the clean-subset threshold in the test is too strict/loose against real numbers, set it to the value the spec intends (our_err ≤ voidx_err on the clean subset) and note the calibration.

- [ ] **Step 5: Commit**

```bash
git add tools/distiller/distill.py tools/distiller/test_distill.py tools/distiller/FINDINGS.md
git commit -m "distiller: end-to-end distill CLI + dataset fidelity vs VoidX"
```

---

### Task 7: Guarded on-device amp upload (C#)

**Files:**
- Modify: `tools/HwCheck/Program.cs`

**Interfaces:** consumes `SonuClient.DReadBlobAsync`/`DWriteChunkAsync`; no Python interface.

- [ ] **Step 1: Add the `--upload-amp` branch**

Add `--upload-amp <vxampPath> <slotIndex>` to `tools/HwCheck/Program.cs`, mirroring the existing guarded write pattern: refuse unless `WritesAllowed`; back up the target `root\amp` slot's current 96-chunk blob to `docs/backups/amp-<slot>-<timestamp>.vxamp` (read via `DReadBlobAsync(@"root\amp", slot, 96)`); write name (chunk 0, zero-padded ASCII from the file's slot name or a provided name), payload chunks 1..96 (128 B each from the 12288-byte file), then terminator chunk -1; read the slot back via `DReadBlobAsync` and confirm byte-equality with the intended payload. Print `RESULT: UPLOAD-AMP OK/FAIL`.

- [ ] **Step 2: Build**

Run: `dotnet build tools/HwCheck`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add tools/HwCheck/Program.cs
git commit -m "HwCheck: guarded --upload-amp for distiller ear-validation"
```

*(On-device ear-check is a manual step for the user: `dotnet run --project tools/HwCheck -- --upload-amp <file> <emptySlot>`, then select the amp on the pedal and A/B against the source tone. Not a CI test.)*

---

### Task 8: Documentation

**Files:**
- Create: `docs/distiller.md`

- [ ] **Step 1: Write `docs/distiller.md`**

Document: the pipeline (runner → prober → fitter → packer), how to run it (`python tools/distiller/distill.py <nam> [out.vxamp]`), the confirmed sample rate, the pinned nonlinearity form, the WH split convention, the clean-subset fidelity results vs VoidX, the v1 scope limits (clean/edge-of-breakup; high-gain ceiling), and the on-device upload/ear-check procedure via `HwCheck --upload-amp`. Link to `docs/vxamp-format.md` and `tools/distiller/FINDINGS.md`.

- [ ] **Step 2: Verify + commit**

Run: `python -m pytest tools/distiller tools/vxamp-re -v` (full suite green).
```bash
git add docs/distiller.md
git commit -m "distiller: usage + results documentation"
```
