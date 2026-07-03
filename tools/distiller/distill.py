"""End-to-end NAM -> vxamp distiller CLI + dataset fidelity — Task 6.

distill(nam_path) -> bytes
    Full 12288-byte `.vxamp` slot from a source `.nam`:

        write_vxamp(loudness_normalize(fit_wh(load_nam_model(nam_path))))

fidelity_vs_nam(nam_path) -> dict
    {"our_err": float, "voidx_err": float | None, "our_vs_voidx": float | None}
    comparing the device-sim response of our fitted tensors — and, for paired
    corpus models, VoidX's decoded tensors — against the NAM's own response.

Loudness normalization (the step that makes distilled amps usable)
-------------------------------------------------------------------
The Task-5 fitter is NAM-faithful in level, but VoidX runs its cascades
~15–198x (median ~52x) hotter than the source NAM: it normalizes to a DEVICE
REFERENCE LOUDNESS. Without matching it, distilled amps sit ~34 dB under
stock corpus amps on the pedal.

Calibration (`device_reference_db()`, corpus-only, read-only): simulate every
paired VoidX tensor set on a fixed 0.3-RMS noise reference signal (the Task-5
drive signal) and take the median output loudness. Measured: **+13.6 dBFS
median, std 3.3 dB** across the 14 pairs — a clustered device target, NOT
per-amp. Cross-check: it does NOT track the NAM's `metadata.loudness` field
(corr ~ 0.05 across pairs), so VoidX is not normalizing from that metadata;
the corpus median is the best available reference.

Application (`loudness_normalize`): simulate OUR fitted tensors on the same
reference signal and fold the dB-matching gain into `g2_fir` (the Task-5
convention; the nonlinearity precedes g2_fir and `apply_nl` is homogeneous of
degree 1, so this scales the output exactly without changing the waveshape).

Fidelity metric (GAIN-, POLARITY- and DELAY-INVARIANT, separate from loudness)
------------------------------------------------------------------------------
err = 0.5 * [ (1 - logmag_corr(linear IRs))          # spectral shape
            + aligned NRMSE(0.3-RMS driven output) ]  # time-domain shape

The linear term probes the small-signal response (impulse; spectrally
equivalent to a sweep for the linear path); the driven term probes the
nonlinear behaviour at guitar level. Before the NRMSE the candidate is
best-lag aligned (+-128 samples, absorbing inaudible bulk delay) and scaled
by a SIGNED least-squares gain (absorbing level and polarity inversion) —
applied identically to our output and VoidX's, so our_err vs voidx_err is a
fair waveshape comparison that does not penalize perceptually irrelevant
differences. (VoidX inverts output polarity on ~half its conversions and
bulk-delays 2-87 samples vs the NAM; the previous positive-RMS-gain,
unaligned NRMSE penalized that maximally and inflated our win.)

CLI
---
    python tools/distiller/distill.py <model.nam> [out.vxamp]   # distill one
    python tools/distiller/distill.py                            # batch report

Corpus-only, READ-ONLY on NAMFiles/; never touches the device.
"""
from __future__ import annotations

import functools
import sys
from pathlib import Path

import numpy as np
from scipy.signal import resample_poly

import device_sim as ds
import fit as ft
import probe
from nam_runner import load_nam_model

try:
    import codec
    import vxamp as vx
    from nam_to_vxamp import write_vxamp
except ModuleNotFoundError:  # standalone use without the pytest conftest shim
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
    import codec
    import vxamp as vx
    from nam_to_vxamp import write_vxamp

# NAM ecosystem default rate. Corpus .nam files all declare 48000; a file that
# omits `sample_rate` must be assumed 48 kHz (NOT the device's 44.1 kHz —
# assuming device rate would silently skip the resampling and frequency-warp
# the distilled amp by ~9%).
NAM_DEFAULT_SAMPLE_RATE = 48000

# Fixed reference/drive signal: identical to the Task-5 fitter's calibration
# signal (seed 0, 16000 samples, 0.3 RMS) so level calibration and fidelity
# probing happen at the same guitar-level drive.
DRIVE_RMS = ft.DRIVE_LEVEL
_REF_N = 16000


def _drive_signal() -> np.ndarray:
    rng = np.random.default_rng(0)
    return (rng.standard_normal(_REF_N) * DRIVE_RMS).astype(np.float32)


def _rms_db(y: np.ndarray) -> float:
    y = np.asarray(y, dtype=np.float64)
    return 20.0 * np.log10(float(np.sqrt(np.mean(y * y))) + 1e-30)


# ---------------------------------------------------------------------------
# loudness normalization
# ---------------------------------------------------------------------------

@functools.lru_cache(maxsize=1)
def device_reference_db() -> float:
    """Device reference loudness: median VoidX output on the reference signal.

    For every paired corpus amp, run the VoidX-fitted tensors through the
    device simulator on the fixed 0.3-RMS reference and measure output dBFS.
    The values cluster (std ~3.3 dB) around a median of ~+13.6 dBFS — the
    device target VoidX normalizes to. (It does NOT track the NAMs'
    `metadata.loudness`: corr ~0.05.)
    """
    x = _drive_signal()
    dbs = [
        _rms_db(ds.simulate(codec.decode(vx.load_vxamp(vf))["tensors"], x))
        for _name, _nam, vf in vx.pairs()
    ]
    return float(np.median(dbs))


def loudness_normalize(tensors: dict) -> dict:
    """Scale the fitted cascade to the device reference loudness.

    Simulates *tensors* on the fixed reference signal and folds the
    dB-matching gain into ``g2_fir`` (Task-5 convention). The nonlinearity
    sits before g2_fir and is homogeneous of degree 1, so this scales the
    output exactly without altering the waveshape.
    """
    y = ds.simulate(tensors, _drive_signal())
    gain = 10.0 ** ((device_reference_db() - _rms_db(y)) / 20.0)
    out = dict(tensors)
    out["g2_fir"] = (np.asarray(tensors["g2_fir"], np.float64) * gain).astype(np.float32)
    return out


# ---------------------------------------------------------------------------
# model loading + fitting (cached per path)
# ---------------------------------------------------------------------------

def _load_model(nam_path):
    model = load_nam_model(nam_path)
    if model.sample_rate is None:  # .nam omitted sample_rate -> NAM default
        model.sample_rate = NAM_DEFAULT_SAMPLE_RATE
    return model


@functools.lru_cache(maxsize=32)
def _distilled(nam_path_str: str):
    """(model, loudness-normalized fitted tensors) for a .nam path. Cached."""
    model = _load_model(nam_path_str)
    return model, loudness_normalize(ft.fit_wh(model))


def distill(nam_path) -> bytes:
    """Distill a source `.nam` into a full 12288-byte `.vxamp` slot."""
    _model, tensors = _distilled(str(Path(nam_path).resolve()))
    return write_vxamp(tensors)


# ---------------------------------------------------------------------------
# fidelity metric (gain-invariant)
# ---------------------------------------------------------------------------

ALIGN_MAX_LAG = 128  # +-samples (~2.9 ms) searched for bulk-delay alignment


def _best_lag(ref: np.ndarray, y: np.ndarray, max_lag: int = ALIGN_MAX_LAG) -> int:
    """Integer lag maximizing |normalized cross-correlation| of y against ref.

    |.| so a polarity-inverted candidate still aligns (the sign is absorbed by
    the signed gain in `_aligned_nrmse`).
    """
    best_lag, best_c = 0, -1.0
    for lag in range(-max_lag, max_lag + 1):
        a = ref[lag:] if lag >= 0 else ref[:lag]
        b = y[: len(a)] if lag >= 0 else y[-lag:]
        denom = float(np.linalg.norm(a)) * float(np.linalg.norm(b))
        if denom < 1e-30:
            continue
        c = abs(float(np.dot(a, b))) / denom
        if c > best_c:
            best_c, best_lag = c, lag
    return best_lag


def _aligned_nrmse(ref: np.ndarray, y: np.ndarray) -> float:
    """Relative L2 error after best-lag alignment + signed least-squares gain.

    The signed gain `g = <ref, y>/<y, y>` (may be negative) absorbs both level
    and polarity inversion; the +-ALIGN_MAX_LAG alignment absorbs bulk delay.
    Both are perceptually irrelevant for an amp model, so the score measures
    waveshape only. Applied identically to every candidate (ours and VoidX's).
    """
    ref = np.asarray(ref, np.float64)
    y = np.asarray(y, np.float64)
    n = min(len(ref), len(y))
    ref, y = ref[:n], y[:n]
    lag = _best_lag(ref, y)
    a = ref[lag:] if lag >= 0 else ref[:lag]
    b = y[: len(a)] if lag >= 0 else y[-lag:]
    a_nrm = float(np.linalg.norm(a))
    b_sq = float(np.dot(b, b))
    if a_nrm < 1e-12 or b_sq < 1e-24:
        return 0.0 if a_nrm < 1e-12 and b_sq < 1e-24 else 1.0
    g = float(np.dot(a, b)) / b_sq
    return float(np.linalg.norm(g * b - a) / a_nrm)


def _shape_err(ref_ir, dev_ir, ref_driven, dev_driven) -> float:
    """0.5*[(1 - log-mag spectral corr of IRs) + aligned driven NRMSE]."""
    spec = 1.0 - probe.logmag_corr(ref_ir, dev_ir)
    nrmse = _aligned_nrmse(ref_driven, dev_driven)
    return 0.5 * (spec + nrmse)


def _nam_ir_dev(model) -> np.ndarray:
    """NAM small-signal IR, resampled to the device rate (Task-5 convention)."""
    ir = probe.linear_ir_of_model(model, n=8192)
    sr = int(model.sample_rate)
    if sr != ds.SAMPLE_RATE:
        ir = resample_poly(np.asarray(ir, dtype=np.float64), ds.SAMPLE_RATE, sr)
    return np.asarray(ir, dtype=np.float64)


def _voidx_tensors_for(nam_path: Path) -> dict | None:
    for name, _nam, vf in vx.pairs():
        if name == nam_path.stem:
            return codec.decode(vx.load_vxamp(vf))["tensors"]
    return None


def fidelity_vs_nam(nam_path) -> dict:
    """Gain/polarity/delay-invariant fidelity of our tensors (and VoidX's) vs the NAM.

    Returns {"our_err", "voidx_err", "our_vs_voidx"}; the VoidX entries are
    None for models without a paired corpus `.vxamp`.
    """
    nam_path = Path(nam_path)
    model, ours = _distilled(str(nam_path.resolve()))

    x = _drive_signal()
    nam_ir = _nam_ir_dev(model)
    nam_driven = ft._model_ref_at_device_rate(model, x)

    our_ir = ds.linear_ir(ours)
    our_driven = ds.simulate(ours, x)
    our_err = _shape_err(nam_ir, our_ir, nam_driven, our_driven)

    voidx_err = our_vs_voidx = None
    tv = _voidx_tensors_for(nam_path)
    if tv is not None:
        vx_ir = ds.linear_ir(tv)
        vx_driven = ds.simulate(tv, x)
        voidx_err = _shape_err(nam_ir, vx_ir, nam_driven, vx_driven)
        our_vs_voidx = _shape_err(vx_ir, our_ir, vx_driven, our_driven)

    return {"our_err": our_err, "voidx_err": voidx_err, "our_vs_voidx": our_vs_voidx}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _print_row(name: str, r: dict) -> None:
    ve = f"{r['voidx_err']:.4f}" if r["voidx_err"] is not None else "  n/a "
    ov = f"{r['our_vs_voidx']:.4f}" if r["our_vs_voidx"] is not None else "  n/a "
    win = ""
    if r["voidx_err"] is not None:
        win = "  <- ours better" if r["our_err"] <= r["voidx_err"] else "  (VoidX better)"
    print(f"  {name[:46]:46s} our_err {r['our_err']:.4f}   voidx_err {ve}   "
          f"our_vs_voidx {ov}{win}")


def _batch_report() -> None:
    print(f"Device reference loudness (0.3-RMS drive): "
          f"{device_reference_db():+.2f} dBFS (corpus median)\n")
    print(f"Per-amp fidelity vs the source NAM ({len(vx.pairs())} pairs):")
    wins = total = 0
    for name, nam_path, vf in vx.pairs():
        s = float(np.ravel(codec.decode(vx.load_vxamp(vf))["tensors"]["nlmix"])[0])
        r = fidelity_vs_nam(nam_path)
        loud = _rms_db(ds.simulate(_distilled(str(nam_path.resolve()))[1],
                                   _drive_signal()))
        _print_row(f"{name} [vx nlmix {s:.2f}]", r)
        print(f"  {'':46s} our loudness {loud:+.2f} dBFS")
        if r["voidx_err"] is not None:
            total += 1
            wins += r["our_err"] <= r["voidx_err"]
    print(f"\nour_err <= voidx_err on {wins}/{total} pairs")


def main(argv: list[str]) -> int:
    if not argv:
        _batch_report()
        return 0
    nam_path = Path(argv[0])
    out_path = Path(argv[1]) if len(argv) > 1 else Path(nam_path.stem + ".vxamp")
    out_path.write_bytes(distill(nam_path))
    print(f"wrote {out_path} ({out_path.stat().st_size} bytes)")
    _print_row(nam_path.stem, fidelity_vs_nam(nam_path))
    return 0


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main(sys.argv[1:]))
