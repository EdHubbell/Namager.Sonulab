"""Device-model forward simulator (linear path) — Task 2.

Architecture (Wiener-Hammerstein FIR-cascade, from tools/vxamp-re/arch.py):

    y = g2_fir ⊛ nl(pre_fir ⊛ x)

Tensor keys (arch.tensor_sizes / arch.split_body):
    pre_fir        1024 float32 FIR taps   — short tone-shaping filter
    g2_header      3 float32               — TLV metadata (NOT used in DSP path)
    g2_fir         1024 float32 FIR taps   — long cab/speaker IR
    nlmix_header   4 float32               — TLV metadata (NOT used in DSP path)
    nlmix          1 float32 scalar        — nonlinear-mix amount (0 = linear)

The header tensors (g2_header, nlmix_header) and the nlmix scalar are metadata.
Task 4 will wire them into the `nl` callable; the linear path ignores them.

Public API
----------
simulate(tensors, x, nl=None) -> np.ndarray
    Device forward model. nl=None means identity (exact for nlmix==0 corpus amps).

linear_ir(tensors) -> np.ndarray
    Cascade impulse response pre_fir ⊛ g2_fir (length 2047 = 1024+1024-1).

tensors_from_vxamp(slot_bytes) -> dict
    Convenience: codec.decode(slot_bytes)["tensors"].
"""
from __future__ import annotations

from typing import Callable

import numpy as np
from scipy.signal import lfilter

try:
    import codec
except ModuleNotFoundError:
    import sys
    from pathlib import Path
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
    import codec


def _apply_fir(taps: np.ndarray, x: np.ndarray) -> np.ndarray:
    """Causal FIR filtering, same-length output: y[n] = sum_k taps[k] * x[n-k]."""
    taps = np.ravel(taps).astype(np.float64)
    x_in = x.astype(np.float64)
    return lfilter(taps, 1.0, x_in).astype(np.float32)


def simulate(
    tensors: dict,
    x: np.ndarray,
    nl: Callable[[np.ndarray], np.ndarray] | None = None,
) -> np.ndarray:
    """Run the device forward model: y = g2_fir ⊛ nl(pre_fir ⊛ x).

    Parameters
    ----------
    tensors:
        Dict from arch.split_body / tensors_from_vxamp. Required keys:
        ``pre_fir`` and ``g2_fir``; the header and nlmix tensors are ignored
        in the linear path (Task 4 will use them).
    x:
        Input audio, 1-D float array.
    nl:
        Nonlinearity callable applied between the two FIR stages. If ``None``
        (default) the identity is used — this is exact for corpus amps whose
        ``nlmix`` scalar is 0. Task 4 will supply a saturating nonlinearity.

    Returns
    -------
    np.ndarray
        Same shape and dtype (float32) as *x*.
    """
    x = np.asarray(x, dtype=np.float32).ravel()

    # Stage 1: pre_fir tone-shaping FIR
    mid = _apply_fir(tensors["pre_fir"], x)

    # Stage 2: nonlinearity (identity when nl is None)
    if nl is not None:
        mid = nl(mid).astype(np.float32)

    # Stage 3: g2_fir cab / speaker IR
    y = _apply_fir(tensors["g2_fir"], mid)

    return y.reshape(x.shape)


def linear_ir(tensors: dict) -> np.ndarray:
    """Return the cascade linear impulse response: np.convolve(pre_fir, g2_fir).

    Length is len(pre_fir) + len(g2_fir) - 1 (= 2047 for 1024-tap corpus amps).
    This equals simulate(tensors, impulse, nl=None)[:len(impulse)] when
    impulse is long enough to capture the full response.
    """
    pre = np.ravel(tensors["pre_fir"]).astype(np.float64)
    g2 = np.ravel(tensors["g2_fir"]).astype(np.float64)
    return np.convolve(pre, g2).astype(np.float32)


def tensors_from_vxamp(slot_bytes: bytes) -> dict:
    """Decode a 12288-byte vxamp slot and return its tensor dict.

    Convenience wrapper for ``codec.decode(slot_bytes)["tensors"]``.
    Tensor keys: ``pre_fir``, ``g2_header``, ``g2_fir``, ``nlmix_header``,
    ``nlmix``.  See ``arch.DEVICE_ARCH`` for full documentation.
    """
    return codec.decode(slot_bytes)["tensors"]
