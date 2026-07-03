import vxamp as vx
import analyze_layout as al

def _bodies():
    return [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]

def test_first_diff_is_at_body_start():
    datas = [vx.load_vxamp(f) for f in vx.vxamp_files()]
    assert al.first_diff_offset(datas) == vx.BODY_OFFSET  # 32

def test_variance_map_length_and_range():
    vm = al.variance_map(_bodies())
    assert len(vm) == vx.BODY_SIZE
    assert max(vm) <= len(vx.vxamp_files())
    assert min(vm) >= 1

def test_constant_islands_are_reported():
    # any body offsets identical across all 20 models are structural, not weights
    consts = al.constant_offsets(_bodies())
    assert isinstance(consts, list)
    # sanity: constants are a small minority of the 8224-byte body
    assert len(consts) < vx.BODY_SIZE // 2
