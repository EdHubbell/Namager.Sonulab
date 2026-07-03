import numpy as np
import pytest

import vxamp as vx
import codec
import nam_runner as nr
import device_sim as ds
import nonlinearity as nl


def test_nl_is_identity_when_scalar_zero():
    x = np.linspace(-1, 1, 501).astype(np.float64)
    y = nl.apply_nl(x, header=np.zeros(4), scalar=0.0)
    assert np.allclose(y, x, atol=1e-9)


def test_nl_is_identity_when_scalar_zero_ignores_header():
    # header is fixed TLV metadata in the corpus; passing junk must not matter.
    x = np.linspace(-1, 1, 501).astype(np.float64)
    y = nl.apply_nl(x, header=np.array([1e9, -3.0, 7.0, 42.0]), scalar=0.0)
    assert np.array_equal(y, x)


def test_nl_saturates_and_stays_finite_when_driven():
    rng = np.random.default_rng(0)
    x = rng.standard_normal(4096) * 3.0
    y = nl.apply_nl(x, header=np.zeros(4), scalar=0.4)
    assert np.all(np.isfinite(y))
    # A soft clip mix compresses peaks relative to the linear path.
    assert np.max(np.abs(y)) <= np.max(np.abs(x)) + 1e-9


def test_simulate_none_matches_explicit_apply_nl():
    for name, nam_path, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        x = (np.random.default_rng(1).standard_normal(4000).astype(np.float32)) * 0.3
        y_default = ds.simulate(t, x, nl=None)
        y_explicit = ds.simulate(
            t, x, nl=lambda z: nl.apply_nl(z, t["nlmix_header"], t["nlmix"])
        )
        assert np.array_equal(y_default, y_explicit)
        break


def test_driven_amp_fidelity_improves_with_nl_vs_linear():
    # For a driven corpus amp, the FULL device sim (with nl) must match the NAM
    # better than the linear-only sim -- proving the pinned nl captures real
    # behaviour.
    for name, nam_path, vf in vx.pairs():
        t = codec.decode(vx.load_vxamp(vf))["tensors"]
        if abs(float(np.ravel(t["nlmix"])[0])) < 1e-6:
            continue
        model = nr.load_nam_model(nam_path)
        x = (np.random.default_rng(3).standard_normal(8000).astype(np.float32)) * 0.3
        ref = model.process(x)
        y_lin = ds.simulate(t, x, nl=lambda z: z)
        y_nl = ds.simulate(t, x, nl=None)  # None -> pinned apply_nl
        e_lin = np.linalg.norm(y_lin - ref)
        e_nl = np.linalg.norm(y_nl - ref)
        assert e_nl <= e_lin + 1e-6
        return
    pytest.skip("no driven amp in corpus")
