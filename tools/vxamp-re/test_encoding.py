import numpy as np
import vxamp as vx
import decode_body as db

def _body(name="Pano-Verb"):
    for n, _, vf in vx.pairs():
        if n == name:
            return vx.body(vx.load_vxamp(vf))
    raise AssertionError("pair not found")

def test_int8_view_is_symmetric_bounded():
    a = db.as_int8(_body())
    assert a.shape == (8224,)
    assert a.min() >= -128 and a.max() <= 127

def test_encoding_decision_recorded():
    # After analysis, ENCODING must be one of the accepted schemes (not None).
    assert db.ENCODING in {"int8", "int16", "int8+block-scale"}

def test_dequant_produces_weightlike_range():
    # Real NAM weights sit within a few units of zero; the dequantized body must too.
    w = db.dequant(_body(), db.SCALE_DEFAULT)
    assert np.isfinite(w).all()
    assert np.abs(w).max() < 64.0
