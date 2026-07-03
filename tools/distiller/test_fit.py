import numpy as np
import vxamp as vx, codec, arch
import device_sim as ds, fit as ft


class _SimModel:
    # a fake "NAM" whose response IS a known device model, so the fitter must recover it
    def __init__(self, tensors): self.t = tensors; self.arch = "WaveNet"; self.sample_rate = ds.SAMPLE_RATE
    def process(self, x): return ds.simulate(self.t, x, nl=None)


def test_fit_returns_correct_tensor_shape():
    t = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))["tensors"]
    out = ft.fit_wh(_SimModel(t))
    assert {k: np.asarray(v).size for k, v in out.items()} == {n: c for n, c in arch.tensor_sizes()}


def test_fit_recovers_a_linear_device_model():
    # pick a linear corpus amp; a model that already IS that device model must fit back to ~itself
    for f in vx.vxamp_files():
        t = codec.decode(vx.load_vxamp(f))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-9:
            break
    fitted = ft.fit_wh(_SimModel(t))
    x = np.random.default_rng(4).standard_normal(8000).astype(np.float32) * 0.1
    ref = ds.simulate(t, x, nl=None); got = ds.simulate(fitted, x, nl=None)
    assert np.linalg.norm(got - ref) / (np.linalg.norm(ref) + 1e-9) < 0.05
