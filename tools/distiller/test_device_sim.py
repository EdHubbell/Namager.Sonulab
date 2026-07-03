import numpy as np
import vxamp as vx, codec
import device_sim as ds


def _linear_pair_tensors():
    # a corpus amp with nlmix == 0 (fully linear) — pick by scanning
    for f in vx.vxamp_files():
        t = codec.decode(vx.load_vxamp(f))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-9:
            return t
    raise AssertionError("no linear amp in corpus")


def test_simulate_linear_matches_convolution_of_firs():
    t = _linear_pair_tensors()
    x = np.zeros(4096, dtype=np.float32); x[0] = 1.0     # impulse -> output is the cascade IR
    y = ds.simulate(t, x, nl=None)
    ir = ds.linear_ir(t)
    n = min(len(y), len(ir))
    assert np.allclose(y[:n], ir[:n], atol=1e-5)


def test_simulate_finite_and_same_length():
    t = _linear_pair_tensors()
    x = np.random.default_rng(1).standard_normal(8000).astype(np.float32) * 0.1
    y = ds.simulate(t, x, nl=None)
    assert y.shape == x.shape and np.isfinite(y).all()
