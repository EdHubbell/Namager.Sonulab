import numpy as np
import vxamp as vx, codec
import nam_runner as nr, device_sim as ds, probe as pb


def test_voidx_vxamp_linear_response_matches_its_nam():
    # For a paired (nam, VoidX vxamp): the vxamp's linear cascade IR (small-signal) must resemble the
    # NAM's small-signal IR IF the sample rate is right. This both exercises the prober AND confirms rate.
    name, nam_path, vxamp_path = next(iter(vx.pairs()))
    model = nr.load_nam_model(nam_path)
    nam_ir = pb.linear_ir_of_model(model)
    t = codec.decode(vx.load_vxamp(vxamp_path))["tensors"]
    dev_ir = ds.linear_ir(t)
    corr = pb.logmag_corr(nam_ir, dev_ir)
    assert corr > 0.7   # VoidX's own fit correlates strongly with the NAM's linear response


def test_logmag_corr_self_is_one():
    ir = np.random.default_rng(2).standard_normal(2048)
    assert pb.logmag_corr(ir, ir) > 0.999
