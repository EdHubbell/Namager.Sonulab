import numpy as np
import pytest

import vxamp as vx
import decode_body as db
import arch


def test_tensor_sizes_sum_to_body_elements():
    total = sum(n for _, n in arch.tensor_sizes())
    # Task 3: the body decodes to 2056 float32-LE elements (NOT 8224 int8).
    assert total == db.ELEMENT_COUNTS["float32-le"] == 2056


def test_split_body_covers_every_tensor():
    body = vx.body(vx.load_vxamp(vx.vxamp_files()[0]))
    parts = arch.split_body(body)
    assert {n for n, _ in arch.tensor_sizes()} == set(parts.keys())


def test_split_body_slice_sizes_and_exact_coverage():
    body = vx.body(vx.load_vxamp(vx.vxamp_files()[0]))
    parts = arch.split_body(body)
    for name, n in arch.tensor_sizes():
        assert len(parts[name]) == n, name
    # concatenating the slices in declared order reproduces the full decode
    cat = np.concatenate([parts[name] for name, _ in arch.tensor_sizes()])
    assert np.array_equal(cat, db.as_float32(body))


def test_chunk_headers_parse_on_all_bodies():
    for f in vx.vxamp_files():
        body = vx.body(vx.load_vxamp(f))
        chunks = arch.parse_chunks(body)
        assert [(t, ln) for t, ln, _ in chunks] == [("G2", 0x100C), ("nlmix", 0x14)]
        g2 = dict((t, p) for t, _, p in chunks)
        assert len(g2["G2"]) == 1024
        assert len(g2["nlmix"]) == 1


def test_pre_fir_tail_is_zero_padding_in_corpus():
    for f in vx.vxamp_files():
        parts = arch.split_body(vx.body(vx.load_vxamp(f)))
        assert np.all(parts["pre_fir"][1008:] == 0.0)


def test_scalars_are_bounded_and_finite():
    for f in vx.vxamp_files():
        parts = arch.split_body(vx.body(vx.load_vxamp(f)))
        nl = float(parts["nlmix"][0])
        assert np.isfinite(nl) and 0.0 <= nl < 1.0
