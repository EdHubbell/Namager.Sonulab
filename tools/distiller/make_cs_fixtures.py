"""Generate fixtures + goldens for the C# port (Sonulab.Distill) from the Python oracle.

Usage (repo root, corpus machine):
    python tools/distiller/make_cs_fixtures.py            # committed fixtures
    python tools/distiller/make_cs_fixtures.py --corpus   # + gitignored corpus goldens

Everything here is READ-ONLY on NAMFiles/ and the existing distiller modules.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))

import distill  # noqa: E402
import vxamp as vx  # noqa: E402
from nam_runner import load_nam_model  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "Sonulab.Distill.Tests" / "fixtures"
RESOURCES = ROOT / "src" / "Sonulab.Distill" / "Resources"
CORPUS_GOLDENS = ROOT / "tests" / "Sonulab.Distill.Tests" / "goldens-corpus"


def write_drive_signal() -> None:
    """The fixed 0.3-RMS reference/drive signal (fit.py + distill.py, seed 0)."""
    x = (np.random.default_rng(0).standard_normal(16000) * 0.3).astype(np.float32)
    RESOURCES.mkdir(parents=True, exist_ok=True)
    (RESOURCES / "drive_signal.f32").write_bytes(x.tobytes())
    print(f"drive_signal.f32: {x.size} float32, rms={float(np.sqrt(np.mean(x*x))):.6f}")


def make_synthetic_nam() -> Path:
    """Small standard-WaveNet .nam with deterministic weights (seed 42).
    1 layer group: channels=4, kernel 3, dilations [1,2,4,8], Tanh, ungated,
    head_size 1, head_bias True -> 314 weights incl. trailing head_scale."""
    rng = np.random.default_rng(42)
    n_weights = 4 + 4 * (48 + 4 + 4 + 16 + 4) + 4 + 1   # rechannel + 4 layers + head(+bias)
    weights = (rng.standard_normal(n_weights) * 0.3).astype(np.float32).tolist()
    head_scale = 0.75
    nam = {
        "version": "0.5.4",
        "architecture": "WaveNet",
        "sample_rate": 48000,
        "config": {
            "layers": [{
                "input_size": 1, "condition_size": 1, "channels": 4,
                "kernel_size": 3, "dilations": [1, 2, 4, 8],
                "activation": "Tanh", "gated": False,
                "head_size": 1, "head_bias": True,
            }],
            "head": None,
        },
        "weights": weights + [head_scale],
    }
    FIXTURES.mkdir(parents=True, exist_ok=True)
    p = FIXTURES / "synthetic.nam"
    p.write_text(json.dumps(nam), encoding="utf-8")
    return p


def write_process_golden(nam_path: Path) -> None:
    model = load_nam_model(nam_path)
    n = np.arange(256)
    x = (0.4 * np.sin(2 * np.pi * 220.0 * n / 48000)).astype(np.float32)
    y = model.process(x)
    (FIXTURES / "golden_process.json").write_text(json.dumps({
        "input": [float(v) for v in x],
        "output": [float(v) for v in y],
        "receptive_field": int(model.receptive_field),
    }), encoding="utf-8")
    print(f"golden_process.json: rf={model.receptive_field}, out[0..2]={y[:3]}")


def write_distill_goldens(nam_path: Path) -> None:
    blob = distill.distill(nam_path)
    (FIXTURES / "synthetic.golden.vxamp").write_bytes(blob)
    r = distill.fidelity_vs_nam(nam_path)
    (FIXTURES / "golden_metrics.json").write_text(json.dumps({
        "our_err": r["our_err"],
        "device_reference_db": distill.device_reference_db(),
    }), encoding="utf-8")
    print(f"synthetic golden: {len(blob)} B, our_err={r['our_err']:.6f}, "
          f"ref_db={distill.device_reference_db()!r}")


def verify_header_constants() -> None:
    """Guard the baked TLV header constants (VxampFormat.cs) against the actual corpus."""
    import codec
    t = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))["tensors"]
    g2h = np.asarray(t["g2_header"], dtype="<f4").tobytes().hex()
    nlh = np.asarray(t["nlmix_header"], dtype="<f4").tobytes().hex()
    print(f"corpus g2_header bytes   : {g2h}")
    print(f"corpus nlmix_header bytes: {nlh}")
    assert g2h == "0c1000000000000047320000", f"g2_header mismatch: {g2h}"
    assert nlh == "14000000000000006e6c6d6978000000", f"nlmix_header mismatch: {nlh}"


def write_corpus_goldens() -> None:
    CORPUS_GOLDENS.mkdir(parents=True, exist_ok=True)
    metrics = {}
    for name, nam_path, _vf in vx.pairs():
        blob = distill.distill(nam_path)
        (CORPUS_GOLDENS / f"{name}.golden.vxamp").write_bytes(blob)
        (CORPUS_GOLDENS / f"{name}.nam.path.txt").write_text(str(nam_path), encoding="utf-8")
        metrics[name] = distill.fidelity_vs_nam(nam_path)["our_err"]
        print(f"  {name}: our_err={metrics[name]:.6f}")
    (CORPUS_GOLDENS / "metrics.json").write_text(json.dumps(metrics), encoding="utf-8")


if __name__ == "__main__":
    write_drive_signal()
    verify_header_constants()
    p = make_synthetic_nam()
    write_process_golden(p)
    write_distill_goldens(p)
    if "--corpus" in sys.argv:
        write_corpus_goldens()
    print("DONE")
