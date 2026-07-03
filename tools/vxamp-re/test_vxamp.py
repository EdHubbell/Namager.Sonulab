import struct
import vxamp as vx

def test_all_slots_have_expected_shape():
    files = vx.vxamp_files()
    assert len(files) == 20
    for f in files:
        data = vx.load_vxamp(f)
        assert len(data) == vx.SLOT_SIZE
        assert len(vx.payload(data)) == vx.PAYLOAD_SIZE
        assert vx.size_field(data) == vx.PAYLOAD_SIZE  # 0x2040
        assert len(vx.body(data)) == vx.BODY_SIZE

def test_header_is_constant_across_all_models():
    headers = {vx.header(vx.load_vxamp(f)).hex() for f in vx.vxamp_files()}
    assert headers == {vx.HEADER_HEX}

def test_pairs_resolve_sources():
    ps = vx.pairs()
    # at least the FullCaptures models pair cleanly by exact stem
    names = {name for name, _, _ in ps}
    assert "Pano-Verb" in names
    assert len(ps) >= 12
    for _, nam_path, vx_path in ps:
        assert nam_path.exists() and vx_path.exists()

def test_nam_weights_shapes():
    nam = vx.load_nam(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    ws = vx.nam_weights(nam)
    labels = {lbl: len(w) for lbl, w in ws}
    assert labels == {"sub0": 1871, "sub1": 12146}
