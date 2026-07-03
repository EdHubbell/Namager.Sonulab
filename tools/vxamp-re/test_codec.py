"""Tests for codec.py — byte-exact round-trip decoder/encoder (Task 5, TDD).

RED → GREEN: run before and after implementing codec.py.
"""
import numpy as np
import pytest

import vxamp as vx
import arch


def test_roundtrip_is_byte_exact_for_every_slot():
    import codec
    for f in vx.vxamp_files():
        data = vx.load_vxamp(f)
        assert codec.roundtrip_ok(data), f"roundtrip mismatch: {f.name}"


def test_decode_exposes_named_tensors():
    import codec
    d = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))
    assert "tensors" in d and len(d["tensors"]) > 0
    assert set(d["tensors"].keys()) == {name for name, _ in arch.tensor_sizes()}


def test_decode_chunks_matches_expected():
    import codec
    d = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))
    assert d["chunks"] == [("G2", 0x100C), ("nlmix", 0x14)]


def test_decode_header_is_32_bytes():
    import codec
    d = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))
    assert len(d["header"]) == vx.HEADER_SIZE
    assert d["header"] == bytes.fromhex(vx.HEADER_HEX)


def test_decode_raw_body_is_8224_deobfuscated_bytes():
    import codec
    import decode_body as db
    body_obf = vx.body(vx.load_vxamp(vx.vxamp_files()[0]))
    d = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))
    assert len(d["raw_body"]) == vx.BODY_SIZE
    # raw_body is the de-obfuscated plaintext; re-applying XOR gives back the original
    assert bytes(np.frombuffer(d["raw_body"], np.uint8) ^ db.keystream(vx.BODY_SIZE)) == body_obf
