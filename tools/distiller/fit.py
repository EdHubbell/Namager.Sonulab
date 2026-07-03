"""Wiener-Hammerstein fitter — Task 5 (DISCOVERY).

Fits a NAM model's response into the device tensor parameterization

    y = g2_fir (*) nl(pre_fir (*) x)          (see device_sim / arch.DEVICE_ARCH)

`fit_wh(model)` returns a tensor dict with exactly `arch.tensor_sizes()` names
and sizes: pre_fir(1024), g2_header(3), g2_fir(1024), nlmix_header(4), nlmix(1).

Fit procedure
-------------
1. **Linear cascade.** Probe the model's small-signal impulse response
   (`probe.linear_ir_of_model`), resample to the device rate (44100 Hz) if the
   model runs at another rate (corpus NAMs are 48 kHz — skipping this would
   frequency-warp every distilled amp by ~9%). Then design the two FIRs with
   two candidate splits and keep the one whose cascade reproduces the target
   IR with lower L2 error:

   - *delta split*: `pre_fir` = unit impulse, `g2_fir` = first 1024 taps of the
     linear IR. Exact-gain, tail-truncation error only (<= ~2% for corpus-like
     responses whose energy sits in the first 1024 taps).
   - *min-phase split* (VoidX-like): `pre_fir` = short (64-tap) minimum-phase
     FIR fitted to the cepstrally-smoothed magnitude of the response (broadband
     tone + gain), `g2_fir` = regularized spectral deconvolution of the full IR
     by that pre (the cab/coloration detail). This mirrors the corpus
     convention where pre_fir is near-delta (>96% of energy in the first 50
     taps) and carries the gross gain.

2. **Nonlinearity + level calibration.** Drive the model with 0.3-RMS noise
   and grid-search the `nlmix` scalar in [0, 0.7] (through
   `nonlinearity.apply_nl`), with the output gain RMS-matched per candidate so
   the metric measures *waveshape* rather than raw level. (A raw-error grid
   slams to the 0.7 ceiling for every real NAM: all corpus NAMs compress
   heavily at drive level relative to their small-signal gain, so maximum
   compression always wins on raw error — the gain-domination problem from
   Task 4.) Snaps to exactly 0 when the nonlinear stage does not measurably
   improve the shape match (clean/linear models), because nlmix == 0 is the
   corpus convention for clean amps and gives the exact identity path.

   The winning RMS gain is folded into `g2_fir`: the cascade's output level is
   calibrated to the NAM **at the 0.3-RMS drive level**, not at small signal.
   This mirrors VoidX (its cascades carry a gross level offset vs the NAM's
   small-signal gain — Task 4 finding — because guitar-level response is what
   matters; corpus NAMs' driven gain is far below their small-signal gain).
   For an exactly linear model the calibration is a no-op (ratio ~1).

3. **Headers.** `g2_header` / `nlmix_header` are fixed TLV chunk metadata,
   byte-identical across all 20 corpus models (Task 4). They are copied from a
   decoded corpus vxamp so the packed container is valid.

Normalization convention (recorded in FINDINGS.md ## Task 5): the cascade's
spectral *shape* is the NAM's small-signal response; its output *level* is
RMS-calibrated to the NAM at the 0.3-RMS drive level (folded into g2_fir).
The delta split carries all gain in g2_fir; the min-phase split carries the
broadband gain in pre_fir (VoidX-like). pre_fir taps 1008..1023 are zero
(corpus invariant) under both splits.
"""
from __future__ import annotations

import numpy as np
from scipy.signal import lfilter, resample_poly

import device_sim as ds
import nonlinearity as nl_mod
import probe

try:
    import arch
    import codec
    import vxamp as vx
except ModuleNotFoundError:  # pragma: no cover - direct execution convenience
    import sys
    from pathlib import Path
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
    import arch
    import codec
    import vxamp as vx

N_TAPS = 1024          # pre_fir / g2_fir length (arch.tensor_sizes)
PRE_ZERO_TAIL = 1008   # corpus invariant: pre_fir taps 1008..1023 are 0.0
N_PRE_SHORT = 64       # min-phase pre length (corpus pre_fir is near-delta, ~50 taps)
NLMIX_MAX = 0.7        # corpus nlmix range is [0, 0.67]
NAM_DEFAULT_SAMPLE_RATE = 48000  # NAM ecosystem default; a model with sample_rate=None is assumed 48 kHz

_headers_cache: dict | None = None


def _corpus_headers() -> dict:
    """The fixed TLV header tensors (identical across all corpus amps, Task 4)."""
    global _headers_cache
    if _headers_cache is None:
        t = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))["tensors"]
        _headers_cache = {
            "g2_header": np.array(t["g2_header"], dtype=np.float32).copy(),
            "nlmix_header": np.array(t["nlmix_header"], dtype=np.float32).copy(),
        }
    return _headers_cache


# ---------------------------------------------------------------------------
# linear cascade design
# ---------------------------------------------------------------------------

def _pad_taps(taps: np.ndarray, n: int = N_TAPS) -> np.ndarray:
    """Zero-pad / truncate to n float32 taps."""
    taps = np.ravel(np.asarray(taps, dtype=np.float32))
    out = np.zeros(n, dtype=np.float32)
    out[: min(n, taps.size)] = taps[:n]
    return out


def _minphase_pre(ir: np.ndarray, n_pre: int = N_PRE_SHORT, n_fft: int = 8192,
                  n_lifter: int = 32) -> np.ndarray:
    """Short minimum-phase FIR matching the cepstrally-smoothed magnitude of ir.

    Homomorphic (real-cepstrum) method: smooth log|H| by keeping only the low
    n_lifter quefrencies, fold the cepstrum to minimum phase, exponentiate,
    truncate to n_pre taps.
    """
    h = np.zeros(n_fft)
    m = min(ir.size, n_fft)
    h[:m] = np.asarray(ir, dtype=np.float64)[:m]
    mag = np.maximum(np.abs(np.fft.fft(h)), 1e-9)
    cep = np.real(np.fft.ifft(np.log(mag)))
    # smooth: keep quefrencies |q| < n_lifter
    cep_s = np.zeros_like(cep)
    cep_s[:n_lifter] = cep[:n_lifter]
    cep_s[-(n_lifter - 1):] = cep[-(n_lifter - 1):]
    # fold to minimum phase
    fold = np.zeros_like(cep_s)
    fold[0] = cep_s[0]
    fold[1: n_fft // 2] = 2.0 * cep_s[1: n_fft // 2]
    fold[n_fft // 2] = cep_s[n_fft // 2]
    h_min = np.real(np.fft.ifft(np.exp(np.fft.fft(fold))))
    return h_min[:n_pre]


def _deconv_g2(target: np.ndarray, pre: np.ndarray, n_out: int = N_TAPS,
               eps: float = 1e-3) -> np.ndarray:
    """Regularized spectral deconvolution: g2 such that pre (*) g2 ~= target."""
    n_fft = 1
    while n_fft < target.size + n_out:
        n_fft *= 2
    T = np.fft.fft(target, n_fft)
    P = np.fft.fft(np.asarray(pre, dtype=np.float64), n_fft)
    p2 = np.abs(P) ** 2
    G = T * np.conj(P) / (p2 + eps * float(p2.max()))
    return np.real(np.fft.ifft(G))[:n_out]


def _cascade_err(pre: np.ndarray, g2: np.ndarray, target: np.ndarray) -> float:
    """Relative L2 error of the cascade IR vs the target IR."""
    casc = np.convolve(np.asarray(pre, np.float64), np.asarray(g2, np.float64))
    n = max(casc.size, target.size)
    c = np.zeros(n); c[:casc.size] = casc
    t = np.zeros(n); t[:target.size] = np.asarray(target, np.float64)
    return float(np.linalg.norm(c - t) / (np.linalg.norm(t) + 1e-12))


def _design_linear(ir_dev: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Split the device-rate linear IR into (pre_fir, g2_fir), 1024 taps each.

    Tries the delta split and the min-phase (VoidX-like) split; returns the
    one whose cascade reproduces the target IR with lower relative L2 error.
    """
    target = np.asarray(ir_dev, dtype=np.float64)[: 2 * N_TAPS - 1]

    # candidate A: delta pre, truncated IR in g2 (exact-gain, L2-optimal for
    # a fixed delta pre)
    pre_a = np.zeros(N_TAPS, dtype=np.float64)
    pre_a[0] = 1.0
    g2_a = target[:N_TAPS]
    err_a = _cascade_err(pre_a, g2_a, target)

    # candidate B: short min-phase pre + deconvolved g2 (corpus-like split)
    try:
        pre_b = _minphase_pre(target)
        g2_b = _deconv_g2(target, pre_b)
        err_b = _cascade_err(pre_b, g2_b, target)
    except (ValueError, FloatingPointError):  # pragma: no cover - degenerate IR
        err_b = np.inf
        pre_b = pre_a
        g2_b = g2_a

    if err_b < err_a:
        pre, g2 = pre_b, g2_b
    else:
        pre, g2 = pre_a, g2_a

    pre = _pad_taps(pre)
    pre[PRE_ZERO_TAIL:] = 0.0  # corpus invariant (trivially true for both splits)
    return pre, _pad_taps(g2)


# ---------------------------------------------------------------------------
# nonlinearity fit
# ---------------------------------------------------------------------------

def _model_ref_at_device_rate(model, x_dev: np.ndarray) -> np.ndarray:
    """Run the model on device-rate input, resampling in/out if rates differ."""
    sr = getattr(model, "sample_rate", None)
    if sr is None:
        sr = NAM_DEFAULT_SAMPLE_RATE  # treat omitted sample_rate as 48 kHz (NAM ecosystem default)
    if int(sr) == ds.SAMPLE_RATE:
        return np.asarray(model.process(x_dev), dtype=np.float64)[: x_dev.size]
    x_m = resample_poly(x_dev.astype(np.float64), int(sr), ds.SAMPLE_RATE)
    y_m = np.asarray(model.process(x_m.astype(np.float32)), dtype=np.float64)
    y = resample_poly(y_m, ds.SAMPLE_RATE, int(sr))
    out = np.zeros(x_dev.size)
    out[: min(y.size, x_dev.size)] = y[: x_dev.size]
    return out


DRIVE_LEVEL = 0.3      # RMS drive level for the nl fit / level calibration


def _fit_nl(model, pre: np.ndarray, g2: np.ndarray,
            nlmix_header: np.ndarray) -> tuple[float, float]:
    """Fit (nlmix, output_gain) against the model driven at DRIVE_LEVEL.

    For each candidate scalar s the simulated output is RMS-matched to the
    reference before scoring, so the grid measures waveshape fit, not level
    (raw error would reward maximum compression for every real NAM — the
    Task 4 gain-domination problem). Snaps s to exactly 0 when the best
    nonzero scalar does not improve the shape match by more than 0.5%
    (clean amps must stay exactly linear).

    Returns (nlmix, gain) where gain is the RMS level-calibration factor to
    fold into g2_fir (~1.0 for an exactly linear model).
    """
    rng = np.random.default_rng(0)
    x = (rng.standard_normal(16000) * DRIVE_LEVEL).astype(np.float32)
    ref = _model_ref_at_device_rate(model, x)
    ref_rms = float(np.sqrt(np.mean(ref * ref)))

    mid = lfilter(np.asarray(pre, np.float64), 1.0, x.astype(np.float64))
    g2f = np.asarray(g2, np.float64)

    def score(s: float) -> tuple[float, float]:
        y = lfilter(g2f, 1.0, nl_mod.apply_nl(mid, nlmix_header, s))
        y_rms = float(np.sqrt(np.mean(y * y)))
        if y_rms < 1e-12 or ref_rms < 1e-12:  # degenerate: no level match
            return float(np.linalg.norm(y - ref)), 1.0
        a = ref_rms / y_rms
        return float(np.linalg.norm(a * y - ref)), a

    e0, a0 = score(0.0)
    best_s, best_e, best_a = 0.0, e0, a0
    for s in np.arange(0.01, NLMIX_MAX + 1e-9, 0.01):
        e, a = score(float(s))
        if e < best_e:
            best_s, best_e, best_a = float(s), e, a

    if best_e > e0 * (1.0 - 0.005):  # no material nonlinear improvement
        return 0.0, a0
    return best_s, best_a


# ---------------------------------------------------------------------------
# public API
# ---------------------------------------------------------------------------

def fit_wh(model) -> dict:
    """Fit a NAM model into the device tensor parameterization.

    Parameters
    ----------
    model:
        Object with ``.process(x) -> y`` (float32, same length) and
        ``.sample_rate`` (Hz; resampled to the device's 44100 Hz when it
        differs — corpus NAMs are native 48 kHz).

    Returns
    -------
    dict
        Tensors with exactly ``arch.tensor_sizes()`` names and sizes:
        ``pre_fir`` (1024), ``g2_header`` (3), ``g2_fir`` (1024),
        ``nlmix_header`` (4), ``nlmix`` (1). float32 arrays.
    """
    # 1) small-signal linear IR, at the device rate
    ir = probe.linear_ir_of_model(model, n=8192)
    sr = getattr(model, "sample_rate", None)
    if sr is None:
        sr = NAM_DEFAULT_SAMPLE_RATE  # treat omitted sample_rate as 48 kHz (NAM ecosystem default)
    if int(sr) != ds.SAMPLE_RATE:
        ir = resample_poly(np.asarray(ir, dtype=np.float64),
                           ds.SAMPLE_RATE, int(sr))

    # 2) linear cascade split
    pre, g2 = _design_linear(ir)

    # 3) fixed TLV headers (corpus-constant, Task 4)
    headers = _corpus_headers()

    # 4) nlmix scalar + output-level calibration at the drive level
    s, gain = _fit_nl(model, pre, g2, headers["nlmix_header"])
    g2 = (g2.astype(np.float64) * gain).astype(np.float32)

    out = {
        "pre_fir": pre,
        "g2_header": headers["g2_header"].copy(),
        "g2_fir": g2,
        "nlmix_header": headers["nlmix_header"].copy(),
        "nlmix": np.array([s], dtype=np.float32),
    }
    sizes = dict(arch.tensor_sizes())
    assert {k: v.size for k, v in out.items()} == sizes
    return out
