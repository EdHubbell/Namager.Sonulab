import json

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


def test_aligned_nrmse_invariance():
    """_aligned_nrmse is invariant to gain, polarity, and delay within ±128,
    and correctly penalizes beyond-window delay and uncorrelated noise."""
    rng = np.random.default_rng(42)
    ref = rng.standard_normal(4000)
    n = len(ref)

    # exact match → 0
    assert dl._aligned_nrmse(ref, ref) < 1e-6

    # polarity inversion → 0 (signed gain absorbs it)
    assert dl._aligned_nrmse(ref, -ref) < 1e-6

    # positive gain → 0
    assert dl._aligned_nrmse(ref, 3.7 * ref) < 1e-6

    # negative gain → 0
    assert dl._aligned_nrmse(ref, -2.5 * ref) < 1e-6

    # delay within ±128 window (+50 samples) → 0
    y_delay50 = np.zeros(n)
    y_delay50[50:] = ref[: n - 50]
    assert dl._aligned_nrmse(ref, y_delay50) < 1e-6

    # combined: delay + polarity flip + gain → 0
    y_combo = np.zeros(n)
    y_combo[30:] = ref[: n - 30]
    assert dl._aligned_nrmse(ref, -1.8 * y_combo) < 1e-6

    # beyond-window delay (+200 samples) → large (> 0.5), correctly penalized
    y_delay200 = np.zeros(n)
    y_delay200[200:] = ref[: n - 200]
    assert dl._aligned_nrmse(ref, y_delay200) > 0.5

    # uncorrelated noise vs ref → near 1.0
    noise = rng.standard_normal(n)
    assert dl._aligned_nrmse(ref, noise) > 0.8


def test_missing_sample_rate_defaults_to_nam_48k(tmp_path):
    # A .nam that omits sample_rate must be treated as 48 kHz (NAM ecosystem
    # default), NOT the device's 44.1 kHz (that would skip resampling and
    # frequency-warp the distilled amp by ~9%).
    nam = json.loads((vx.corpus_root() / "FullCaptures" / "Twin Reverb SM57.nam")
                     .read_text(encoding="utf-8"))
    nam.pop("sample_rate", None)
    p = tmp_path / "no_rate.nam"
    p.write_text(json.dumps(nam), encoding="utf-8")
    assert dl._load_model(p).sample_rate == 48000
