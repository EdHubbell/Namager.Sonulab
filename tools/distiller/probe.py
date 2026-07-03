"""Response prober for NAM models — Task 3.

Provides small-signal impulse response measurement, log-magnitude spectrum,
and correlation metric for comparing NAM and device model responses.

Public API
----------
linear_ir_of_model(model, n=4096, amp=1e-3) -> np.ndarray
    Small-signal impulse response of a NAM model.

logmag(ir, n_fft=4096) -> np.ndarray
    Log-magnitude spectrum (rFFT magnitude in dB).

logmag_corr(a_ir, b_ir) -> float
    Pearson correlation of two IRs' log-magnitude spectra over the shared band.
"""
from __future__ import annotations

import numpy as np


def linear_ir_of_model(
    model,
    n: int = 4096,
    amp: float = 1e-3,
) -> np.ndarray:
    """Small-signal impulse response of a NAM model.

    Feeds a tiny impulse (amplitude ``amp``) through ``model.process``, then
    normalizes out ``amp`` so the result is the linear IR (unit-impulse
    response in the small-signal regime).

    Parameters
    ----------
    model:
        NamModel with a ``.process(x: np.ndarray) -> np.ndarray`` method.
        The model is assumed to be prewarmed and DC-removed (as ``NamModel``
        from ``nam_runner`` guarantees).
    n:
        Length of the impulse buffer in samples.  Should exceed the model's
        receptive field so the full transient is captured.
    amp:
        Impulse amplitude.  Small enough to stay in the linear regime
        (NAM models saturate; at 1e-3 the response is essentially linear
        for the corpus models).

    Returns
    -------
    np.ndarray
        Linear impulse response, length ``n``, dtype float32.
    """
    x = np.zeros(n, dtype=np.float32)
    x[0] = np.float32(amp)
    y = model.process(x)
    return (np.asarray(y, dtype=np.float64) / float(amp)).astype(np.float32)


def logmag(ir: np.ndarray, n_fft: int = 4096) -> np.ndarray:
    """Log-magnitude spectrum of an impulse response.

    Computes the rFFT of ``ir`` (zero-padded or truncated to ``n_fft``
    samples) and returns the magnitude in dB.

    Parameters
    ----------
    ir:
        Impulse response array (1-D, any real dtype).
    n_fft:
        FFT size.  ``ir`` is zero-padded if shorter, or truncated if longer.

    Returns
    -------
    np.ndarray
        rFFT magnitude in dB, shape ``(n_fft // 2 + 1,)``, dtype float64.
        Floored at 1e-12 before taking log to avoid ``-inf``.
    """
    ir_f = np.asarray(ir, dtype=np.float64).ravel()
    if ir_f.size < n_fft:
        ir_f = np.pad(ir_f, (0, n_fft - ir_f.size))
    else:
        ir_f = ir_f[:n_fft]
    mag = np.abs(np.fft.rfft(ir_f))
    mag = np.maximum(mag, 1e-12)   # floor before log
    return 20.0 * np.log10(mag)


def logmag_corr(
    a_ir: np.ndarray,
    b_ir: np.ndarray,
    n_fft: int = 4096,
) -> float:
    """Pearson correlation of two IRs' log-magnitude spectra over a shared band.

    Uses the full rFFT output (DC to Nyquist) as the shared band; both IRs
    are measured at the same ``n_fft`` so the bins correspond.

    Guards against constant vectors (std < 1e-12) and NaN — returns 0.0 in
    those degenerate cases rather than raising.

    Parameters
    ----------
    a_ir, b_ir:
        Impulse responses to compare.
    n_fft:
        FFT size passed to :func:`logmag`.

    Returns
    -------
    float
        Pearson correlation in [-1, 1].  Returns 0.0 on degenerate input.
    """
    a = logmag(a_ir, n_fft)
    b = logmag(b_ir, n_fft)

    a_std = float(np.std(a))
    b_std = float(np.std(b))

    if a_std < 1e-12 or b_std < 1e-12:
        return 0.0

    corr = float(np.corrcoef(a, b)[0, 1])
    return corr if np.isfinite(corr) else 0.0
