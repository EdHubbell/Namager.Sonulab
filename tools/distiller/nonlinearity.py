"""Device nonlinearity — Task 4 (DISCOVERY).

The device forward model is a Wiener-Hammerstein FIR cascade

    y = g2_fir (*) nl(pre_fir (*) x)

where ``nl`` is a static-ish saturating stage gated by the ``nlmix`` scalar
(0 = fully linear, up to ~0.67 in the corpus). This module pins ``nl``.

WHAT THE CORPUS PINS (firm)
---------------------------
- ``nl`` is a **one-parameter family** in the ``nlmix`` scalar. The four
  ``nlmix_header`` floats are NOT tunable parameters: they are the literal TLV
  chunk header of the ``nlmix`` chunk — ``u32 len=0x14, u32 reserved=0,
  tag "nlmix\\0\\0\\0"`` (bytes ``14 00 00 00 00 00 00 00 6e 6c 6d 69 78 00 00 00``),
  **identical for all 20 corpus models**. ``apply_nl`` therefore ignores
  ``header`` except to keep the discovery-task signature; the scalar alone
  parameterizes the nonlinearity.
- ``nlmix == 0`` must reduce to **exact identity** (7 clean corpus models rely
  on this; several downstream tasks assert it bit-for-bit).
- The VoidX distiller (``native_add.dll``) that fits ``nlmix`` names the
  nonlinear basis "**squareMix**" and drives it from a per-level / per-frequency
  harmonic-distortion measurement (debug exports ``DBG_squareMix.txt``,
  ``DBG_levelsDistortionPwr.txt``, ``DBG_linearDiff.txt``,
  ``DBG_frequenciesOutputLevels.txt``). Single-tone probing of the driven NAMs
  confirms a saturating stage that grows even + odd harmonics with level.

WHAT THE CORPUS DOES NOT PIN (see FINDINGS.md ## Task 4 "Concerns")
------------------------------------------------------------------
The exact waveshaper (even "square" vs odd soft-clip character, its bias /
asymmetry, and the drive scaling of its input) is **underdetermined** by the
corpus: the paired NAMs are native 48 kHz while the device runs 44.1 kHz, and
the VoidX FIR cascade carries a gross output-level offset relative to the NAM,
so the nonlinear residual is swamped by rate/level mismatch when compared at the
sample level. Pinning the exact shape needs controlled captures (a linear amp
and a soft-clip drive sweep pushed through VoidX — see FINDINGS).

PINNED FORM (this module)
-------------------------
A drive-normalized soft-clip **mix** — the canonical guitar-amp saturation,
consistent with the "nlmix" *mix* semantics and robust across the corpus (the
pre_fir gain spans ~50x between amps, so the shaper is normalized by the
mid-signal RMS so a single ``nlmix`` behaves comparably for every amp):

    r      = rms(u)                         # drive scale of the mid signal
    nl(u)  = (1 - s)*u + s * r * tanh(u/r)   # s = nlmix

At ``s == 0`` this is exactly ``u``. As ``s`` grows it blends in a soft clip
that compresses peaks and adds harmonics, matching the driven NAMs better than
the linear-only path for all 11 driven corpus amps (Task 4 acceptance gate).

Public API
----------
apply_nl(x, header, scalar) -> np.ndarray
    The pinned nonlinearity. ``header`` (the 4 ``nlmix_header`` floats) is
    accepted for signature stability but is fixed metadata. ``scalar == 0``
    returns ``x`` unchanged (exact identity).
"""
from __future__ import annotations

from pathlib import Path

import numpy as np


def _as_scalar(scalar) -> float:
    """Coerce a scalar / 1-element array / 0-d array to a Python float."""
    arr = np.ravel(np.asarray(scalar, dtype=np.float64))
    return float(arr[0]) if arr.size else 0.0


def apply_nl(x: np.ndarray, header: np.ndarray, scalar: float) -> np.ndarray:
    """Firmware nonlinearity: drive-normalized soft-clip mix gated by ``scalar``.

    Parameters
    ----------
    x:
        Mid-stage signal ``pre_fir (*) x`` (1-D real array).
    header:
        The 4 ``nlmix_header`` floats. Fixed TLV metadata in every corpus
        model (see module docstring); accepted for signature stability and not
        used in the DSP.
    scalar:
        The ``nlmix`` scalar (0..~0.67 in the corpus). ``0`` -> exact identity.

    Returns
    -------
    np.ndarray
        Same shape as ``x``, float64.
    """
    del header  # fixed TLV metadata, intentionally unused
    xf = np.asarray(x, dtype=np.float64)
    s = _as_scalar(scalar)

    # Exact identity for the linear (clean) models — required bit-for-bit.
    if s == 0.0:
        return xf.copy()

    r = float(np.sqrt(np.mean(xf * xf))) if xf.size else 0.0
    if r < 1e-12:
        # Degenerate (silence): the shaper is ~identity near 0 anyway.
        return xf.copy()

    return (1.0 - s) * xf + s * r * np.tanh(xf / r)


# ---------------------------------------------------------------------------
# investigation driver (python nonlinearity.py) — corpus-only, read-only
# ---------------------------------------------------------------------------

def _investigate() -> None:  # pragma: no cover - manual analysis aid
    import sys

    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
    from scipy.signal import lfilter
    import vxamp as vx
    import codec
    import nam_runner as nr
    import device_sim as ds

    print("=" * 78)
    print("Task 4 investigation: pin the device nonlinearity (corpus-only)")
    print("=" * 78)

    # (1) The nlmix_header is fixed TLV metadata, not parameters.
    print("\n[1] nlmix_header bytes across the corpus (should be identical):")
    seen = set()
    for name, _nam, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        hb = np.asarray(t["nlmix_header"], dtype="<f4").tobytes().hex()
        seen.add(hb)
    for hb in sorted(seen):
        pretty = bytes.fromhex(hb)
        print(f"    {hb}   ascii={pretty!r}")
    print(f"    -> {len(seen)} distinct header value(s): fixed TLV chunk header, "
          "NOT tunable params.")

    # (2) Single-tone harmonic character of the driven NAMs (even vs odd).
    print("\n[2] Single-tone harmonics of driven NAMs (48 kHz, 220 Hz):")
    print(f"    {'amp':30s} nlmix   H2/H1   H3/H1")
    SR = 48000
    N = 24000
    f0 = 220.0
    n = np.arange(N)
    win = np.hanning(N)
    for name, nam_path, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        s = float(np.ravel(t["nlmix"])[0])
        if s < 0.05:
            continue
        model = nr.load_nam_model(nam_path)
        x = (0.4 * np.sin(2 * np.pi * f0 * n / SR)).astype(np.float32)
        Y = np.fft.rfft(model.process(x) * win)

        def amp(f):
            k = int(round(f / SR * N))
            return float(np.abs(Y[k - 2:k + 3]).max())

        h1, h2, h3 = amp(f0), amp(2 * f0), amp(3 * f0)
        print(f"    {name[:30]:30s} {s:.3f}  {h2/h1:6.4f}  {h3/h1:6.4f}")

    # (3) Acceptance gate: full sim (nl) vs linear-only sim against the NAM.
    print("\n[3] Fidelity gate: ||sim - NAM|| linear vs pinned-nl (pytest metric):")
    print(f"    {'amp':30s} nlmix   e_lin    e_nl   better?")
    rng = np.random.default_rng(3)
    for name, nam_path, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        s = float(np.ravel(t["nlmix"])[0])
        if s < 1e-6:
            continue
        model = nr.load_nam_model(nam_path)
        x = (rng.standard_normal(8000).astype(np.float32)) * 0.3
        ref = model.process(x)
        y_lin = ds.simulate(t, x, nl=lambda z: z)
        y_nl = ds.simulate(t, x, nl=None)
        e_lin = float(np.linalg.norm(y_lin - ref))
        e_nl = float(np.linalg.norm(y_nl - ref))
        print(f"    {name[:30]:30s} {s:.3f}  {e_lin:7.2f}  {e_nl:7.2f}   "
              f"{'yes' if e_nl <= e_lin + 1e-6 else 'NO'}")

    print("\nSee FINDINGS.md ## Task 4 for the pinned form + concerns.")


if __name__ == "__main__":  # pragma: no cover
    _investigate()
