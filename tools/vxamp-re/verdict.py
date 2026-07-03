"""Repack-vs-refit verdict for the Sonulab vxamp format (Task 6).

VERDICT = "refit": the device does NOT store the source NAM weights.
VoidX-Control DISTILLS the source WaveNet into a compact FIR-cascade
(Wiener-Hammerstein-style) model of a fundamentally different architecture.

Key evidence recorded in compare():
1. Model-class mismatch: device is a FIR-cascade (2048 taps + 9 metadata floats,
   stored in a TLV container), source is WaveNet or SlimmableContainer — completely
   incompatible architectures with no shared tensor shapes.
2. exact_frac ≈ 0.0 across all pairs: source weights are absent from the
   de-obfuscated device float stream; no contiguous run of source weights appears
   in the device body.
3. corr ≈ 0 across all pairs: no linear relationship between source submodel
   weights and the device weight vector — device parameters are derived, not a
   scaled/reordered subset of the source weights.
4. Spectral distillation (CITED from Task 4): pre_fir ⊛ g2_fir approximates the
   WaveNet small-signal IR (median log-mag corr 0.915) — confirming the device
   model is a FIT (distillation), not a byte-exact copy.

Implication: byte-exact .nam → .vxamp reproduction is NOT achievable without
reproducing VoidX's FIR/nonlinearity fitting.  Path forward = sub-project 2:
fit our own FIR-cascade from the NAM impulse response.
"""
from __future__ import annotations

import numpy as np

import vxamp as vx
import decode_body as db
import arch

# The verdict: the device model is a refit (distillation), not a byte-exact repack.
VERDICT = "refit"

# Task 4 spectral distillation correlation — CITED here, NOT recomputed.
# Median log-magnitude correlation of (pre_fir ⊛ g2_fir) vs WaveNet IR
# over 11 Slimmable pairs, from arch.py / FINDINGS.md ## Task 4.
SPECTRAL_CORR_CITED = 0.915


def _longest_contiguous_run(
    src: np.ndarray, device: np.ndarray, tol: float = 1e-4
) -> int:
    """Return the length of the longest contiguous run of ``src`` values that
    appears in sequence inside ``device`` (within ``tol`` per element).

    I.e. the largest k such that there exist d, s with
        |device[d+i] - src[s+i]| <= tol  for all i in [0, k).

    Uses binary search on a sorted copy of ``src`` to find candidate starting
    positions efficiently; the inner extension loop is O(k) and exits on the
    first mismatch — fast in practice when matches are rare.
    """
    src = np.asarray(src, dtype=np.float64)
    device = np.asarray(device, dtype=np.float64)
    M, N = len(device), len(src)
    if M == 0 or N == 0:
        return 0

    best = 0

    # Sort source once for O(log N) candidate lookup per device position.
    src_sorted_idx = np.argsort(src, kind="stable")
    src_sorted = src[src_sorted_idx]

    for d in range(M):
        dval = device[d]
        lo = int(np.searchsorted(src_sorted, dval - tol, side="left"))
        hi = int(np.searchsorted(src_sorted, dval + tol, side="right"))
        for pos in range(lo, hi):
            s = int(src_sorted_idx[pos])
            # Extend the match as far as it goes.
            run = 1
            while (
                d + run < M
                and s + run < N
                and abs(device[d + run] - src[s + run]) <= tol
            ):
                run += 1
            if run > best:
                best = run
                if best >= N:
                    return best  # perfect full-source match — early exit

    return best


def compare(name: str) -> dict:
    """Compare source NAM weights against the device body for the pair ``name``.

    ``name`` must be an element of ``vxamp.pairs()[i][0]``.

    Returns a dict with **at least** the keys required by the tests:

        exact_frac      float   Fraction of source weights that appear verbatim
                                (within 1e-4) as a contiguous run in the
                                de-obfuscated device float stream
                                (decode_body.as_float32).  Expect ≈ 0.0.
        corr            float   Pearson correlation between the device weight
                                vector (decode_body.weights, finite values) and
                                the best-size source submodel weights truncated
                                to the same length.  Expect ≈ 0.
        max_abs_err     float   Max |device - source| on the aligned comparison.
                                Expect large (weights come from different models).

    Additional informational keys:
        device_tensor_sizes     dict  {name: count} from arch.tensor_sizes()
        source_weight_counts    dict  {submodel: count} from vxamp.nam_weights()
        model_class_match       bool  Always False (FIR-cascade ≠ WaveNet / Slim)
        spectral_corr_cited     float CITED from Task 4; NOT recomputed here.
    """
    # Locate the pair by name.
    found = None
    for n, nam_path, vxamp_path in vx.pairs():
        if n == name:
            found = (nam_path, vxamp_path)
            break
    if found is None:
        raise ValueError(f"pair {name!r} not found in vxamp.pairs()")
    nam_path, vxamp_path = found

    # Load files.
    nam = vx.load_nam(nam_path)
    data = vx.load_vxamp(vxamp_path)
    body = vx.body(data)

    # Device float stream (all 2056, for exact_frac check).
    device_floats = db.as_float32(body).astype(np.float64)
    # Device weight vector (metadata islands zeroed, for corr/max_abs_err).
    device_weights = db.weights(body)  # float64, 2056 elements

    # Source weights from the NAM.
    src_groups = vx.nam_weights(nam)  # [(name, list[float])]
    src_arrays = [(nm, np.asarray(w, dtype=np.float64)) for nm, w in src_groups]
    all_src = np.concatenate([a for _, a in src_arrays])

    # ------------------------------------------------------------------
    # exact_frac: longest contiguous run of source weights in device stream.
    # ------------------------------------------------------------------
    run_len = _longest_contiguous_run(all_src, device_floats, tol=1e-4)
    exact_frac = float(run_len) / float(len(all_src)) if len(all_src) > 0 else 0.0

    # ------------------------------------------------------------------
    # corr & max_abs_err: aligned device vs best-matching source submodel.
    # ------------------------------------------------------------------
    dev_finite = device_weights[np.isfinite(device_weights)]
    n_dev = len(dev_finite)

    # Pick the source submodel whose weight count is closest to device count.
    best_src_name, best_src_arr = min(
        src_arrays, key=lambda t: abs(len(t[1]) - n_dev)
    )

    n_align = min(n_dev, len(best_src_arr))
    a = dev_finite[:n_align]
    b = best_src_arr[:n_align]

    if a.std() > 0 and b.std() > 0:
        corr = float(np.corrcoef(a, b)[0, 1])
    else:
        corr = 0.0

    max_abs_err = float(np.max(np.abs(a - b)))

    return {
        "exact_frac": exact_frac,
        "corr": corr,
        "max_abs_err": max_abs_err,
        "device_tensor_sizes": dict(arch.tensor_sizes()),
        "source_weight_counts": {nm: len(arr) for nm, arr in src_arrays},
        "model_class_match": False,
        "spectral_corr_cited": SPECTRAL_CORR_CITED,
    }
