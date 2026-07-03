"""Tests for nam_to_vxamp.py — vxamp container writer + scoped-out .nam distillation (Task 7).

TDD: RED -> GREEN.  Run before and after implementing nam_to_vxamp.py.
"""
from pathlib import Path

import pytest

import vxamp as vx
import arch
import codec


def test_write_vxamp_roundtrips_every_real_model_byte_exact():
    """write_vxamp(decode(slot)["tensors"]) reproduces the original slot byte-for-byte
    across all 20 corpus models.  This proves we can author the container from model
    parameters, not just echo opaque raw_body bytes (which codec.encode already does)."""
    import nam_to_vxamp as n2v

    files = vx.vxamp_files()
    assert len(files) == 20, f"expected 20 corpus files, got {len(files)}"
    for f in files:
        slot = vx.load_vxamp(f)
        tensors = codec.decode(slot)["tensors"]
        produced = n2v.write_vxamp(tensors)
        assert produced == slot, f"byte-exact roundtrip failed for {f.name}"


def test_write_vxamp_produces_valid_container_shape():
    """Output is SLOT_SIZE bytes, starts with the constant header, has correct size field."""
    import nam_to_vxamp as n2v

    slot = vx.load_vxamp(vx.vxamp_files()[0])
    tensors = codec.decode(slot)["tensors"]
    out = n2v.write_vxamp(tensors)

    assert len(out) == vx.SLOT_SIZE, f"expected {vx.SLOT_SIZE} B, got {len(out)}"
    assert vx.header(out).hex() == vx.HEADER_HEX, "header mismatch"
    assert vx.size_field(out) == vx.PAYLOAD_SIZE, f"size field mismatch"


def test_write_vxamp_rejects_wrong_tensor_dict():
    """Passing a dict with a missing tensor, an extra tensor, or a wrong-size tensor
    raises ValueError before any bytes are assembled."""
    import nam_to_vxamp as n2v

    slot = vx.load_vxamp(vx.vxamp_files()[0])
    good_tensors = codec.decode(slot)["tensors"]

    # Missing one required tensor
    bad_missing = {k: v for k, v in good_tensors.items() if k != "nlmix"}
    with pytest.raises(ValueError, match="nlmix"):
        n2v.write_vxamp(bad_missing)

    # Extra unexpected key
    import numpy as np
    bad_extra = dict(good_tensors, unexpected_key=np.zeros(1, dtype="<f4"))
    with pytest.raises(ValueError, match="unexpected_key"):
        n2v.write_vxamp(bad_extra)

    # Wrong size for a tensor
    bad_size = dict(good_tensors)
    name, expected_n = arch.tensor_sizes()[0]  # "pre_fir", 1024
    bad_size[name] = np.zeros(expected_n - 1, dtype="<f4")
    with pytest.raises(ValueError, match=name):
        n2v.write_vxamp(bad_size)


def test_nam_to_vxamp_is_scoped_out():
    """nam_to_vxamp(path) must raise NotImplementedError — NAM->FIR-cascade distillation
    is sub-project 2 and is not implemented here."""
    import nam_to_vxamp as n2v

    with pytest.raises(NotImplementedError):
        n2v.nam_to_vxamp(Path("any_model.nam"))

    # Also works with a string path
    with pytest.raises(NotImplementedError):
        n2v.nam_to_vxamp("any_model.nam")
