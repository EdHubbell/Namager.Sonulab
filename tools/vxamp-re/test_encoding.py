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


# ---- Fix pass: the real encoding is float32-LE under a keystream (see FINDINGS). ----

def _all_bodies():
    return [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]


def test_keystream_reproduces_constant_island():
    # The keystream, recovered from the zero-padding island, must reproduce that
    # island's ciphertext byte-for-byte (island plaintext == float32 0.0 padding).
    # First 64 bytes of the island are float32 0.0 padding (ciphertext == keystream);
    # the island's 12-byte tail holds shared metadata, so only assert the pad region.
    b0 = np.frombuffer(_body(), dtype=np.uint8)
    k = db.keystream(len(b0))
    assert np.array_equal(k[4032:4096], b0[4032:4096])


def test_deobfuscated_padding_decodes_to_float_zero():
    # Every body's padding island must de-obfuscate to exact float32 0.0.
    for b in _all_bodies():
        pad = db.as_float32(b)[1008:1024]
        assert np.all(pad == 0.0)


def test_float32_element_count():
    assert db.as_float32(_body()).shape == (2056,)
    assert db.ELEMENT_DTYPE == "float32-le"
    assert db.ELEMENT_COUNTS["float32-le"] == 2056


def test_weights_are_zero_peaked_and_bounded():
    # The acceptance test the review demanded: decoded weights must look like real
    # NN weights -- ZERO-PEAKED (heavy excess kurtosis) and bounded -- NOT flat.
    for b in _all_bodies():
        w = db.weights(b)
        assert np.isfinite(w).all()
        assert np.abs(w).max() < 64.0
        core = w[np.abs(w) < 64]
        mu, sd = core.mean(), core.std()
        kurt = np.mean(((core - mu) / sd) ** 4) - 3
        assert kurt > 20.0  # strong zero peak; raw int8 gives ~0 (flat/uniform)


def test_raw_int8_is_flat_not_weightlike():
    # Contrast: the superseded int8 view is uniform-random, NOT zero-peaked.
    a = db.as_int8(_body()).astype(float)
    assert abs(np.mean(np.abs(a)) - 64.0) < 5.0        # uniform expectation ~64
    assert np.mean(np.abs(a) <= 16) < 0.20             # not concentrated near zero
