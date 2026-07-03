"""Loaders + container constants for the Sonulab vxamp amp-model format.
Works from the committed paired corpus in NAMFiles/. Read-only."""
from __future__ import annotations
import json
import struct
from pathlib import Path

SLOT_SIZE = 12288
PAYLOAD_SIZE = 8256
HEADER_SIZE = 32
BODY_OFFSET = 32
BODY_SIZE = PAYLOAD_SIZE - HEADER_SIZE  # 8224
HEADER_HEX = "4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be"


def corpus_root() -> Path:
    # tools/vxamp-re/vxamp.py -> parents[2] == repo root
    return Path(__file__).resolve().parents[2] / "NAMFiles"


def load_vxamp(path) -> bytes:
    return Path(path).read_bytes()


def payload(data: bytes) -> bytes:
    return data[:PAYLOAD_SIZE]


def header(data: bytes) -> bytes:
    return data[:HEADER_SIZE]


def body(data: bytes) -> bytes:
    return data[BODY_OFFSET:PAYLOAD_SIZE]


def size_field(data: bytes) -> int:
    return struct.unpack_from("<H", data, 0)[0]


def vxamp_files() -> list[Path]:
    return sorted((corpus_root() / "VxampDump").glob("*.vxamp"))


def _vxamp_ampname(path: Path) -> str:
    # "NN - <ampname>.vxamp" -> "<ampname>"
    stem = path.stem
    return stem.split(" - ", 1)[1] if " - " in stem else stem


def load_nam(path) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def nam_weights(nam: dict) -> list[tuple[str, list[float]]]:
    arch = nam["architecture"]
    if arch == "WaveNet":
        return [("root", nam["weights"])]
    if arch == "SlimmableContainer":
        return [
            (f"sub{i}", s["model"]["weights"])
            for i, s in enumerate(nam["config"]["submodels"])
        ]
    raise ValueError(f"unhandled architecture: {arch}")


def pairs() -> list[tuple[str, Path, Path]]:
    fc = corpus_root() / "FullCaptures"
    sources = {p.stem: p for p in fc.rglob("*.nam")}
    out = []
    for vf in vxamp_files():
        name = _vxamp_ampname(vf)
        if name in sources:
            out.append((name, sources[name], vf))
    return out
