import numpy as np
import vxamp as vx
import nam_runner as nr

def test_loads_both_corpus_arches():
    slim = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    assert slim.arch == "SlimmableContainer"
    wave = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Princeton Clean 3 SM57.nam")
    assert wave.arch == "WaveNet"

def test_process_is_finite_same_length_and_causal_quiet():
    m = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    x = np.zeros(2048, dtype=np.float32); x[100] = 1.0   # impulse
    y = m.process(x)
    assert y.shape == x.shape
    assert np.isfinite(y).all()
    assert np.allclose(y[:100], 0.0, atol=1e-6)          # causal: no output before the impulse

def test_small_signal_is_roughly_linear():
    # at tiny amplitude a guitar-amp model is ~linear: doubling input ~doubles output
    m = nr.load_nam_model(vx.corpus_root() / "FullCaptures" / "Twin Reverb SM57.nam")
    x = (np.random.default_rng(0).standard_normal(4000).astype(np.float32)) * 1e-3
    y1 = m.process(x); y2 = m.process(2 * x)
    num = np.linalg.norm(y2 - 2 * y1); den = np.linalg.norm(2 * y1) + 1e-12
    assert num / den < 0.05
