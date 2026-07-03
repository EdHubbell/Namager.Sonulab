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
