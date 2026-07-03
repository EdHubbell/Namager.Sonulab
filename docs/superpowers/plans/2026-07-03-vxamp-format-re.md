# vxamp Format Reverse-Engineering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decode the Sonulab StompStation `vxamp` amp-model container so that VoidX's `.nam → vxamp` conversion can be reproduced (byte-exact if it is a repack; documented well enough for a self-quantizer if it is a re-fit).

**Architecture:** A standalone Python (numpy) analysis harness under `tools/vxamp-re/` works entirely from the committed paired corpus (`NAMFiles/FullCaptures/*.nam` ↔ `NAMFiles/VxampDump/*.vxamp`). It proceeds bottom-up: confirm the container invariants, map constant-vs-varying bytes, determine the element encoding, infer the device architecture, build a byte-exact round-trip decoder/encoder, then render a verdict on repack-vs-refit. Controlled VoidX captures are a human-in-the-loop escalation used only where static analysis stalls. No production C# is written here; that is sub-project 2.

**Tech Stack:** Python 3.11+, numpy, pytest. Read-only corpus already in the repo. (Optional, phase 7 only: the existing `tools/HwCheck` C# harness for one guarded on-device A/B.)

## Global Constraints

- **This sub-project is READ-ONLY on the device**, except the single optional guarded on-device A/B in Task 9 (empty/throwaway amp slot, backed up first, per repo write-safety rules in `CLAUDE.md`).
- **Work only from the committed corpus** in `NAMFiles/`. Do not require the pedal to be attached for Tasks 1–8.
- **Confirmed container facts (verbatim, from `PROTOCOL.md`):** slot = 12288 B; payload = 8256 B; constant 32-B header `4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be`; size field bytes 0–1 = `0x2040` = 8256 (LE u16); per-model body = bytes 32..8255 (8224 B); body is fixed-point quantized (int8 leading hypothesis, int16 not yet ruled out); source weights do NOT appear verbatim.
- **Pairing rule:** a `.vxamp` file is `NN - <ampname>.vxamp`; its source is `NAMFiles/FullCaptures[/Rejected]/<ampname>.nam` when a stem-exact match exists. Not every dumped `.vxamp` has a source in the corpus — use matched pairs only.
- **Every git commit message must end with these two trailer lines** (harness requirement):
  ```
  Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01GVMhjpcXJV1sbeBZnuv8G2
  ```
- Run tests from the repo root with the harness dir on the path: `python -m pytest tools/vxamp-re -v`.
- **Discovery tasks (3, 4, 6) are analysis, not pure TDD.** Their "test" asserts an invariant that must hold once the finding is correct, AND the task must append its conclusion (with evidence) to `tools/vxamp-re/FINDINGS.md`. A reviewer gates the task on the written finding, not just a green test.

### POST-TASK-3 ENCODING CORRECTION (supersedes the int8 assumption in Tasks 4–7)

Task 3 discovered the body is NOT int8. It is **float32-LE weights, XOR-obfuscated** by a keystream
`k[i] = (K0[i%32] − 0x20·(i//32)) mod 256` (K0 recovered from the zero-padding island). Validated:
one keystream de-obfuscates all 20 models into **2056** finite, zero-peaked float32 values, and the
transform round-trips exactly. Downstream tasks MUST consume the real API in `decode_body.py`:
- `deobfuscate(body) -> bytes`, `as_float32(body) -> np.ndarray` (2056 LE float32), `weights(body) ->
  np.ndarray` (metadata islands zeroed), `keystream(n) -> np.ndarray`.
- Authoritative constants: `ELEMENT_DTYPE="float32-le"`, `OBFUSCATION=("xor-keystream",32,-0x20)`,
  `ELEMENT_COUNTS={"float32-le":2056,"int8":8224}` (use **2056 float32**). `ENCODING`/`dequant`/
  `SCALE_DEFAULT` are DEAD legacy paths — do NOT use them.
- The two constant islands (body offsets 4032 len 76, 8204 len 16) are **metadata floats**, not weights.
- Element count for arch-matching is **2056 float32** (minus the metadata floats), NOT 8224 int8.
- There is **no lossy quantization** (float32 is exact), so a byte-exact encoder is achievable IF the
  device weights turn out to be derivable from the source `.nam`. Early evidence: source weights do
  NOT appear in the de-obfuscated stream → likely a **re-fit** (Task 6 decides), i.e. the (c) fallback.

### POST-TASK-4 ARCHITECTURE + VERDICT (reframes Tasks 5–7)

Task 4 established the device model is **not a WaveNet** — it is a **Wiener–Hammerstein FIR-cascade in a
TLV container** (`arch.py`): `pre_fir` (1024 float32 taps) ‖ chunk `"G2"` (len 0x100C: 3-float header +
1024-tap cabinet IR) ‖ chunk `"nlmix"` (len 0x14: 4-float header + 1 mix scalar). Reconciles to 2056
float32 / 8224 bytes; TLV closes on all 20 models; `arch.parse_chunks()`, `arch.split_body()`,
`arch.tensor_sizes()` implement it. **The verdict is therefore REFIT by construction:** VoidX distills
the source NAM (a neural WaveNet) into a *different, cheaper model class*, so byte-exact reproduction from
a `.nam` is not achievable without reproducing VoidX's FIR/nonlinearity fitting. Consequences:
- **Task 5 (codec):** decode via `arch.parse_chunks`/`split_body` + `decode_body.deobfuscate`/`as_float32`
  into named tensors (pre_fir, g2_header, g2_ir, nlmix_header, nlmix_scalar) + the byte-exact raw bytes;
  encode = reassemble tensors → float32-LE bytes → re-obfuscate (`keystream`) → header → pad to 12288.
  `roundtrip_ok(blob)` must be byte-exact for all 20 (the obfuscation round-trips exactly, already shown).
- **Task 6 (verdict):** record `VERDICT="refit"` with evidence — device is a FIR-cascade (different model
  class), source WaveNet weights absent from the de-obfuscated stream, and `pre_fir ⊛ g2_ir` approximates
  the source's linear response (Task 4 spectral corr ≈ 0.915) = a distillation, not a repack. There is NO
  WaveNet-weight alignment (`align_source_to_device` does not apply); the `compare()` metric is the
  spectral/structural evidence, not weight exact-match.
- **Task 7 (encoder):** implement the container **writer** — `write_vxamp(pre_fir, g2_header, g2_ir,
  nlmix_header, nlmix_scalar) -> 12288-byte slot`, validated **byte-exact by round-tripping every real
  blob's decoded tensors** and by container-shape checks. The actual `.nam → FIR-cascade` **fitting
  (distillation)** is **OUT OF SCOPE (sub-project 2)** — expose it as a clearly-documented
  `NotImplementedError` stub (`nam_to_vxamp`) pointing to sub-project 2, so the byte-exact test for the
  refit path is skipped honestly rather than faked.

---

### Task 1: Corpus loader + container invariants

**Files:**
- Create: `tools/vxamp-re/requirements.txt`
- Create: `tools/vxamp-re/vxamp.py`
- Create: `tools/vxamp-re/test_vxamp.py`
- Create: `tools/vxamp-re/FINDINGS.md`

**Interfaces:**
- Produces:
  - `SLOT_SIZE=12288`, `PAYLOAD_SIZE=8256`, `HEADER_SIZE=32`, `BODY_OFFSET=32`, `BODY_SIZE=8224`, `HEADER_HEX` (str)
  - `corpus_root() -> Path`
  - `load_vxamp(path) -> bytes` (full slot bytes)
  - `payload(data) -> bytes`, `header(data) -> bytes`, `body(data) -> bytes`, `size_field(data) -> int`
  - `vxamp_files() -> list[Path]`
  - `load_nam(path) -> dict`, `nam_weights(nam) -> list[tuple[str, list[float]]]`
  - `pairs() -> list[tuple[str, Path, Path]]` → `(name, nam_path, vxamp_path)` for stem-matched pairs

- [ ] **Step 1: Write the requirements file**

Create `tools/vxamp-re/requirements.txt`:
```
numpy>=1.26
pytest>=8.0
```

- [ ] **Step 2: Install deps**

Run: `python -m pip install -r tools/vxamp-re/requirements.txt`
Expected: numpy + pytest installed (or "already satisfied").

- [ ] **Step 3: Write the failing test**

Create `tools/vxamp-re/test_vxamp.py`:
```python
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
```

- [ ] **Step 4: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_vxamp.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'vxamp'` (or import errors).

- [ ] **Step 5: Write the implementation**

Create `tools/vxamp-re/vxamp.py`:
```python
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
    return json.loads(Path(path).read_text())


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
```

- [ ] **Step 6: Run test to verify it passes**

Run: `python -m pytest tools/vxamp-re/test_vxamp.py -v`
Expected: PASS (4 tests).

- [ ] **Step 7: Seed the findings log**

Create `tools/vxamp-re/FINDINGS.md`:
```markdown
# vxamp RE — running findings

## Task 1 — container invariants (confirmed)
- 20 slots, each 12288 B; payload 8256 B; size field = 0x2040.
- 32-B header constant across all models: `4020...c4be`.
- Body = bytes 32..8255 (8224 B).
- Clean source pairs available: (fill count from `pairs()`).
```

- [ ] **Step 8: Commit**

```bash
git add tools/vxamp-re
git commit -m "vxamp-re: corpus loader + confirmed container invariants"
```

---

### Task 2: Constant-vs-varying byte map + entropy profile

**Files:**
- Create: `tools/vxamp-re/analyze_layout.py`
- Create: `tools/vxamp-re/test_layout.py`

**Interfaces:**
- Consumes: `vxamp.vxamp_files`, `vxamp.body`, `vxamp.BODY_SIZE`
- Produces:
  - `variance_map(bodies: list[bytes]) -> list[int]` — per-body-offset count of distinct byte values across models
  - `constant_offsets(bodies) -> list[int]` — body offsets identical across all models
  - `first_diff_offset(datas) -> int` — first payload offset (absolute) where models differ
  - `byte_entropy(bodies) -> list[float]` — per-offset Shannon entropy (bits) across models

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_layout.py`:
```python
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_layout.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'analyze_layout'`.

- [ ] **Step 3: Write the implementation**

Create `tools/vxamp-re/analyze_layout.py`:
```python
"""Structural map of the vxamp body: which offsets are constant vs vary across
models, and per-offset entropy. Guides where weights vs structure/scales live."""
from __future__ import annotations
import math
from collections import Counter
import vxamp as vx


def first_diff_offset(datas: list[bytes]) -> int:
    n = min(len(d) for d in datas)
    for i in range(n):
        if len({d[i] for d in datas}) > 1:
            return i
    return n


def variance_map(bodies: list[bytes]) -> list[int]:
    n = min(len(b) for b in bodies)
    return [len({b[i] for b in bodies}) for i in range(n)]


def constant_offsets(bodies: list[bytes]) -> list[int]:
    return [i for i, v in enumerate(variance_map(bodies)) if v == 1]


def byte_entropy(bodies: list[bytes]) -> list[float]:
    n = min(len(b) for b in bodies)
    out = []
    for i in range(n):
        counts = Counter(b[i] for b in bodies)
        total = sum(counts.values())
        h = -sum((c / total) * math.log2(c / total) for c in counts.values())
        out.append(h)
    return out


def _report():
    bodies = [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]
    vm = variance_map(bodies)
    consts = constant_offsets(bodies)
    print(f"body {len(vm)} B: constant offsets={len(consts)} varying={len(vm) - len(consts)}")
    # print constant islands as (start,len) runs
    runs = []
    start = None
    for i in range(len(vm) + 1):
        is_const = i < len(vm) and vm[i] == 1
        if is_const and start is None:
            start = i
        elif not is_const and start is not None:
            runs.append((start, i - start))
            start = None
    print("constant islands (offset,len):", runs[:40])


if __name__ == "__main__":
    _report()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest tools/vxamp-re/test_layout.py -v`
Expected: PASS (3 tests).

- [ ] **Step 5: Generate the report + record findings**

Run: `python tools/vxamp-re/analyze_layout.py`
Then append the printed constant-island runs to `tools/vxamp-re/FINDINGS.md` under a `## Task 2` heading, noting: total constant vs varying byte counts, and any regular stride in the constant islands (a stride hints at per-block scale factors interleaved with quantized weights).

- [ ] **Step 6: Commit**

```bash
git add tools/vxamp-re/analyze_layout.py tools/vxamp-re/test_layout.py tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: body variance/entropy map + constant-island report"
```

---

### Task 3: Element encoding + scale-factor determination (DISCOVERY)

**Files:**
- Create: `tools/vxamp-re/decode_body.py`
- Create: `tools/vxamp-re/test_encoding.py`

**Interfaces:**
- Consumes: `vxamp.body`, `analyze_layout.constant_offsets`
- Produces:
  - `as_int8(body: bytes) -> "np.ndarray"` (signed, shape (8224,))
  - `as_int16(body: bytes) -> "np.ndarray"` (LE signed, shape (4112,))
  - `dequant(body: bytes, scale: float) -> "np.ndarray"` — decode under the chosen element type × scale
  - module constant `ENCODING` set to the determined scheme (`"int8"` / `"int16"` / `"int8+block-scale"`) once known
  - module constant `SCALE_SPEC` describing where scales live (`"global-constant"`, `("interleaved", stride)`, or `"per-tensor-table@<offset>"`)

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_encoding.py`:
```python
import numpy as np
import vxamp as vx
import decode_body as db

def _body(name="Pano-Verb"):
    for n, _, vf in vx.pairs():
        if n == name:
            return vx.body(vx.load_vxamp(vf))
    raise AssertionError("pair not found")

def test_int8_view_is_symmetric_bounded():
    a = db.as_int8(_body())
    assert a.shape == (8224,)
    assert a.min() >= -128 and a.max() <= 127

def test_encoding_decision_recorded():
    # After analysis, ENCODING must be one of the accepted schemes (not None).
    assert db.ENCODING in {"int8", "int16", "int8+block-scale"}

def test_dequant_produces_weightlike_range():
    # Real NAM weights sit within a few units of zero; the dequantized body must too.
    w = db.dequant(_body(), db.SCALE_DEFAULT)
    assert np.isfinite(w).all()
    assert np.abs(w).max() < 64.0
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_encoding.py -v`
Expected: FAIL — module missing / `ENCODING is None`.

- [ ] **Step 3: Write the decoder scaffold + analysis driver**

Create `tools/vxamp-re/decode_body.py`:
```python
"""Determine the vxamp body element encoding and scale placement, then expose a
dequantizer. ENCODING / SCALE_SPEC / SCALE_DEFAULT are set from the analysis in
_investigate() and frozen here once the reviewer accepts the finding."""
from __future__ import annotations
import numpy as np
import vxamp as vx

# ---- Determined by Task 3 analysis (see FINDINGS.md). ----
ENCODING = None            # -> "int8" | "int16" | "int8+block-scale"
SCALE_SPEC = None          # -> "global-constant" | ("interleaved", stride) | "per-tensor-table@<off>"
SCALE_DEFAULT = 1.0 / 127  # starting guess; refine from single-weight controlled capture if needed


def as_int8(body: bytes) -> np.ndarray:
    return np.frombuffer(body, dtype=np.int8)


def as_int16(body: bytes) -> np.ndarray:
    return np.frombuffer(body, dtype="<i2")


def dequant(body: bytes, scale: float) -> np.ndarray:
    if ENCODING in (None, "int8", "int8+block-scale"):
        return as_int8(body).astype(np.float64) * scale
    if ENCODING == "int16":
        return as_int16(body).astype(np.float64) * scale
    raise ValueError(ENCODING)


def _investigate():
    """Print the evidence needed to fix ENCODING/SCALE_SPEC. Run, then edit the
    module constants above to match, then append the conclusion to FINDINGS.md."""
    bodies = [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]
    b0 = bodies[0]
    a8 = as_int8(b0)
    a16 = as_int16(b0)
    print("int8 : min", a8.min(), "max", a8.max(), "unique", len(np.unique(a8)))
    print("int16: min", a16.min(), "max", a16.max(), "unique", len(np.unique(a16)))
    # int8 histogram peaks at +/-127 would indicate clipping (fixed global scale);
    # smooth interior distribution favors int8. A bimodal low-cardinality column set
    # at a fixed stride indicates interleaved per-block scales.
    hist = np.bincount((a8.astype(int) + 128), minlength=256)
    print("int8 clip counts: [-128]=%d [127]=%d" % (hist[0], hist[255]))
    # look for a repeating stride of high-magnitude bytes (candidate scale slots)
    mags = np.abs(a8.astype(int))
    for stride in (16, 32, 64, 128, 256):
        idx = np.arange(0, len(mags), stride)
        print(f"stride {stride:4d}: mean|slot0|={mags[idx].mean():.1f} vs overall={mags.mean():.1f}")


if __name__ == "__main__":
    _investigate()
```

- [ ] **Step 4: Run the investigation**

Run: `python tools/vxamp-re/decode_body.py`
Read the output. Decide the encoding:
- int8 min/max spanning full range with a smooth interior and low clip counts → `ENCODING="int8"`.
- Heavy clip counts at ±127 → a fixed global scale that clips large weights; check whether a per-block/per-tensor scale removes the clipping → `"int8+block-scale"` and set `SCALE_SPEC` to the stride found.
- int16 giving a cleaner distribution and int8 looking like noise → `ENCODING="int16"`.

- [ ] **Step 5: Fix the constants + record the finding**

Edit `decode_body.py`: set `ENCODING`, `SCALE_SPEC`, and `SCALE_DEFAULT` to the determined values. Append to `FINDINGS.md` under `## Task 3` the evidence (min/max/clip counts/stride table) and the conclusion.

- [ ] **Step 6: If static evidence is ambiguous — ESCALATE to controlled captures**

If Step 4 cannot distinguish int8 vs int16, or cannot locate the scale, STOP and do **Task 3E** (below) before continuing. Otherwise skip Task 3E.

- [ ] **Step 7: Run tests to verify they pass**

Run: `python -m pytest tools/vxamp-re/test_encoding.py -v`
Expected: PASS (3 tests) with the constants now set.

- [ ] **Step 8: Commit**

```bash
git add tools/vxamp-re/decode_body.py tools/vxamp-re/test_encoding.py tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: determine body element encoding + scale placement"
```

---

### Task 3E (CONDITIONAL): Controlled-capture probe generator + analysis

Run this ONLY if Task 3 (or later Task 4/6) stalls on an ambiguity that near-identical inputs would resolve. It is human-in-the-loop: it produces synthetic `.nam` files and exact capture steps, the **user** runs them through VoidX with USBPcap, then the analysis diffs the results.

**Files:**
- Create: `tools/vxamp-re/make_probes.py`
- Create: `tools/vxamp-re/CAPTURE-STEPS.md`
- Create: `tools/vxamp-re/analyze_probes.py`

**Interfaces:**
- Consumes: a real corpus `.nam` as a template (`vxamp.load_nam`)
- Produces: probe `.nam` files in `tools/vxamp-re/probes/`; `analyze_probes.diff_pair(vxamp_a, vxamp_b) -> list[int]` (offsets that changed)

- [ ] **Step 1: Write the probe generator**

Create `tools/vxamp-re/make_probes.py`:
```python
"""Generate synthetic .nam probes from a template model. Each isolates one input
so its footprint in the vxamp is unambiguous. Output -> tools/vxamp-re/probes/."""
from __future__ import annotations
import copy, json
from pathlib import Path
import vxamp as vx

OUT = Path(__file__).resolve().parent / "probes"


def _set_all(nam, value):
    out = copy.deepcopy(nam)
    for _, w in vx.nam_weights(out):
        for i in range(len(w)):
            w[i] = value
    return out


def _set_one(nam, flat_index, value):
    out = copy.deepcopy(nam)
    # walk the same flattening order nam_weights exposes
    remaining = flat_index
    for _, w in vx.nam_weights(out):
        if remaining < len(w):
            w[remaining] = value
            return out
        remaining -= len(w)
    raise IndexError(flat_index)


def main():
    OUT.mkdir(exist_ok=True)
    tmpl = vx.load_nam(vx.corpus_root() / "FullCaptures" / "Pano-Verb.nam")
    (OUT / "all_zero.nam").write_text(json.dumps(_set_all(tmpl, 0.0)))
    (OUT / "all_zero_but0_is_1.nam").write_text(json.dumps(_set_one(_set_all(tmpl, 0.0), 0, 1.0)))
    (OUT / "all_zero_but1_is_1.nam").write_text(json.dumps(_set_one(_set_all(tmpl, 0.0), 1, 1.0)))
    (OUT / "all_zero_but0_is_half.nam").write_text(json.dumps(_set_one(_set_all(tmpl, 0.0), 0, 0.5)))
    print("wrote probes to", OUT)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Generate the probes**

Run: `python tools/vxamp-re/make_probes.py`
Expected: 4 `.nam` files in `tools/vxamp-re/probes/`.

- [ ] **Step 3: Write the capture instructions for the user**

Create `tools/vxamp-re/CAPTURE-STEPS.md` with exact steps: close everything holding COM6; start Wireshark on the USBPcap interface filtering the CH340; in VoidX, upload each `probes/*.nam` to the SAME amp slot one at a time; save each capture as `probe_<name>.pcapng` in the repo parent dir; then run `tools/HwCheck --dump-amps` after each upload to also capture the resulting blob as `probes/out_<name>.vxamp`. (The dump path is the reliable output source; the pcap is a cross-check.)

- [ ] **Step 4: Write the diff analyzer**

Create `tools/vxamp-re/analyze_probes.py`:
```python
"""Diff probe output blobs to localize which body bytes each input weight controls
and the quantization scale. Expects tools/vxamp-re/probes/out_*.vxamp from captures."""
from __future__ import annotations
from pathlib import Path
import numpy as np
import vxamp as vx

P = Path(__file__).resolve().parent / "probes"


def diff_pair(a: bytes, b: bytes) -> list[int]:
    return [i for i in range(min(len(a), len(b))) if a[i] != b[i]]


def main():
    def bod(n):
        return vx.body(vx.load_vxamp(P / n))
    z = bod("out_all_zero.vxamp")
    one0 = bod("out_all_zero_but0_is_1.vxamp")
    half0 = bod("out_all_zero_but0_is_half.vxamp")
    changed = diff_pair(z, one0)
    print("weight[0]=1.0 changed body offsets:", changed)
    if changed:
        off = changed[0]
        v1 = np.frombuffer(one0[off:off+2], dtype=np.int8)[0]
        vh = np.frombuffer(half0[off:off+2], dtype=np.int8)[0]
        print(f"at offset {off}: value@1.0={v1} value@0.5={vh}  => scale≈{0.5/ (vh or 1)}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 5: (After the user captures) run the analyzer + record findings**

Run: `python tools/vxamp-re/analyze_probes.py`
Append the localized offsets + derived scale to `FINDINGS.md`, then return to the task that escalated here and set its constants from this evidence.

- [ ] **Step 6: Commit**

```bash
git add tools/vxamp-re/make_probes.py tools/vxamp-re/CAPTURE-STEPS.md tools/vxamp-re/analyze_probes.py tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: controlled-capture probe generator + diff analyzer"
```

---

### Task 4: Device architecture inference + tensor layout (DISCOVERY)

**Files:**
- Create: `tools/vxamp-re/arch.py`
- Create: `tools/vxamp-re/test_arch.py`

**Interfaces:**
- Consumes: `decode_body.as_int8`/`as_int16`, `analyze_layout.constant_offsets`
- Produces:
  - `DEVICE_ARCH` (dict): the fixed WaveNet config the body encodes (layers, channels, kernel sizes, dilations, head)
  - `tensor_sizes() -> list[tuple[str, int]]` — ordered `(tensor_name, element_count)` summing to the body element count
  - `split_body(body: bytes) -> dict[str, "np.ndarray"]` — body sliced into named tensors per the layout

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_arch.py`:
```python
import vxamp as vx
import arch

def test_tensor_sizes_sum_to_body_elements():
    total = sum(n for _, n in arch.tensor_sizes())
    # int8 -> 8224 elements; int16 -> 4112. Must match the chosen encoding exactly.
    assert total in (8224, 4112)

def test_split_body_covers_every_tensor():
    body = vx.body(vx.load_vxamp(vx.vxamp_files()[0]))
    parts = arch.split_body(body)
    assert {n for n, _ in arch.tensor_sizes()} == set(parts.keys())
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_arch.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Infer the architecture**

Determine `DEVICE_ARCH`. Primary method: the source NAMs whose architecture already matches the 8256-B target size (inspect the `SlimmableContainer` submodel and plain-WaveNet `config`) give candidate layer/channel/kernel/dilation values; compute the WaveNet parameter count for candidates and find the one whose total equals the body element count (8224 for int8, 4112 for int16). Fallback if no candidate fits: extract config constants from `C:\Program Files (x86)\VoidX-Control\data\app.so` (search printable strings for `WaveNet`, `channels`, `kernel`, `dilation`, layer counts).

- [ ] **Step 4: Write the implementation**

Create `tools/vxamp-re/arch.py` with `DEVICE_ARCH` set to the inferred config, and `tensor_sizes()`/`split_body()` computing tensor boundaries from it. (Fill the real values from Step 3 — the WaveNet weight order is: per layer [input mixin, dilated conv kernel, conv bias, 1×1 mixin, condition weights], then the head. Compute each tensor's element count from `DEVICE_ARCH` so `tensor_sizes()` sums to the body element count.)

- [ ] **Step 5: Run test to verify it passes**

Run: `python -m pytest tools/vxamp-re/test_arch.py -v`
Expected: PASS (2 tests).

- [ ] **Step 6: Record findings + commit**

Append `DEVICE_ARCH` and the `tensor_sizes()` table to `FINDINGS.md` under `## Task 4`.
```bash
git add tools/vxamp-re/arch.py tools/vxamp-re/test_arch.py tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: infer device WaveNet arch + tensor layout"
```

---

### Task 5: Byte-exact round-trip decoder/encoder

**Files:**
- Create: `tools/vxamp-re/codec.py`
- Create: `tools/vxamp-re/test_codec.py`

**Interfaces:**
- Consumes: `vxamp.header`, `vxamp.HEADER_HEX`, `arch.split_body`, `arch.tensor_sizes`, `decode_body.dequant`, `decode_body.ENCODING`, `decode_body.SCALE_SPEC`
- Produces:
  - `decode(data: bytes) -> dict` — `{"tensors": {name: float_ndarray}, "scales": {...}, "raw": {name: int_ndarray}}`
  - `encode(decoded: dict) -> bytes` — reconstruct the full 12288-B slot
  - `roundtrip_ok(data: bytes) -> bool` — `encode(decode(data)) == data`

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_codec.py`:
```python
import vxamp as vx
import codec

def test_roundtrip_is_byte_exact_for_every_slot():
    for f in vx.vxamp_files():
        data = vx.load_vxamp(f)
        assert codec.roundtrip_ok(data), f"roundtrip mismatch: {f.name}"

def test_decode_exposes_named_tensors():
    d = codec.decode(vx.load_vxamp(vx.vxamp_files()[0]))
    assert "tensors" in d and len(d["tensors"]) > 0
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_codec.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Write the implementation**

Create `tools/vxamp-re/codec.py`: `decode()` splits header + body, slices the body into raw int tensors via `arch.split_body`, extracts scales per `decode_body.SCALE_SPEC`, and dequantizes. `encode()` reverses it exactly: re-quantize each tensor to the raw ints (round-trip must be lossless because `decode` keeps the raw ints in `d["raw"]`), reassemble body, prepend `bytes.fromhex(vx.HEADER_HEX)`, zero-pad to 12288. `roundtrip_ok()` compares. (Keeping `raw` ints in the decoded dict guarantees byte-exact re-encode; the float tensors are for interpretation only.)

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest tools/vxamp-re/test_codec.py -v`
Expected: PASS — byte-exact round-trip for all 20 slots. If any slot fails, the layout/encoding from Tasks 3–4 is wrong; fix there before proceeding.

- [ ] **Step 5: Commit**

```bash
git add tools/vxamp-re/codec.py tools/vxamp-re/test_codec.py
git commit -m "vxamp-re: byte-exact round-trip decoder/encoder for all slots"
```

---

### Task 6: Repack-vs-refit verdict (DISCOVERY — decides sub-project 2)

**Files:**
- Create: `tools/vxamp-re/verdict.py`
- Create: `tools/vxamp-re/test_verdict.py`

**Interfaces:**
- Consumes: `codec.decode`, `arch.DEVICE_ARCH`, `vxamp.pairs`, `vxamp.nam_weights`
- Produces:
  - `align_source_to_device(name) -> "np.ndarray"` — the source `.nam` weights arranged into the device tensor order
  - `compare(name) -> dict` — `{"exact_frac": float, "corr": float, "max_abs_err": float}` between requantized source weights and the blob's decoded tensors
  - module constant `VERDICT` — `"repack"` or `"refit"` — set from the evidence

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_verdict.py`:
```python
import vxamp as vx
import verdict

def test_compare_runs_on_a_matched_pair():
    name = vx.pairs()[0][0]
    r = verdict.compare(name)
    assert set(r) >= {"exact_frac", "corr", "max_abs_err"}
    assert 0.0 <= r["exact_frac"] <= 1.0

def test_verdict_is_decided():
    assert verdict.VERDICT in {"repack", "refit"}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_verdict.py -v`
Expected: FAIL — module missing / `VERDICT is None`.

- [ ] **Step 3: Implement the comparison**

Create `tools/vxamp-re/verdict.py`: pick the source submodel/architecture whose shape matches `DEVICE_ARCH`, arrange its weights into device tensor order (`align_source_to_device`), requantize with the Task-3 encoding+scale, and compare against `codec.decode(blob)["raw"]`/tensors. Compute exact-match fraction, Pearson correlation, and max abs error across matched pairs.

- [ ] **Step 4: Run + interpret**

Run: `python -c "import sys; sys.path.insert(0,'tools/vxamp-re'); import verdict; print({n: verdict.compare(n) for n,_,_ in __import__('vxamp').pairs()})"`
Decide:
- High `exact_frac` (≈1.0) → **repack**: byte-exact conversion is reproducible. Set `VERDICT="repack"`.
- Low exact but high `corr` → quantization/order detail still off; revisit Tasks 3–4.
- Low exact AND low `corr` across all pairs → **refit**: VoidX trains a new model. Set `VERDICT="refit"`.

- [ ] **Step 5: Set VERDICT + record findings**

Set `VERDICT` and append the per-pair metrics + conclusion to `FINDINGS.md` under `## Task 6`. This is the pivotal result for the whole parent project.

- [ ] **Step 6: Run tests + commit**

Run: `python -m pytest tools/vxamp-re/test_verdict.py -v`
Expected: PASS.
```bash
git add tools/vxamp-re/verdict.py tools/vxamp-re/test_verdict.py tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: repack-vs-refit verdict with per-pair evidence"
```

---

### Task 7: Encoder validation (branch on VERDICT)

**Files:**
- Create: `tools/vxamp-re/nam_to_vxamp.py`
- Create: `tools/vxamp-re/test_nam_to_vxamp.py`

**Interfaces:**
- Consumes: `verdict.align_source_to_device`, `codec.encode`, `verdict.VERDICT`
- Produces: `nam_to_vxamp(nam_path) -> bytes` — full 12288-B slot from a source `.nam`

- [ ] **Step 1: Write the failing test**

Create `tools/vxamp-re/test_nam_to_vxamp.py`:
```python
import vxamp as vx
import verdict
import nam_to_vxamp as n2v

def test_matches_voidx_output_when_repack():
    if verdict.VERDICT != "repack":
        import pytest; pytest.skip("refit path: byte-exact not expected")
    name, nam_path, vxamp_path = vx.pairs()[0]
    produced = n2v.nam_to_vxamp(nam_path)
    expected = vx.load_vxamp(vxamp_path)
    assert produced == expected

def test_produces_valid_container_shape_always():
    _, nam_path, _ = vx.pairs()[0]
    out = n2v.nam_to_vxamp(nam_path)
    assert len(out) == vx.SLOT_SIZE
    assert vx.header(out).hex() == vx.HEADER_HEX
    assert vx.size_field(out) == vx.PAYLOAD_SIZE
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tools/vxamp-re/test_nam_to_vxamp.py -v`
Expected: FAIL — module missing.

- [ ] **Step 3: Implement the encoder**

Create `tools/vxamp-re/nam_to_vxamp.py`: load the `.nam`, align weights to device tensor order, requantize (Task 3 scheme), assemble via `codec.encode`. If `VERDICT=="refit"`, the encoder still emits a valid container from a self-quantized fit of the source (byte-exact test is skipped, container-shape test still runs).

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest tools/vxamp-re/test_nam_to_vxamp.py -v`
Expected: PASS (repack: both tests; refit: shape test passes, byte-exact test skipped).

- [ ] **Step 5: Commit**

```bash
git add tools/vxamp-re/nam_to_vxamp.py tools/vxamp-re/test_nam_to_vxamp.py
git commit -m "vxamp-re: .nam -> vxamp encoder validated against captured output"
```

---

### Task 8: Format documentation

**Files:**
- Create: `docs/vxamp-format.md`
- Modify: `PROTOCOL.md` (the `vxamp` container section — replace the "OPEN" question with the answer + a link)

**Interfaces:** none (documentation).

- [ ] **Step 1: Write `docs/vxamp-format.md`**

Document the full container from the working codec: header field table (offsets 0–31), body element encoding, scale-factor placement, exact tensor order with element counts, `DEVICE_ARCH`, and the repack-vs-refit verdict with evidence. Include a worked example (one model, source `.nam` → bytes).

- [ ] **Step 2: Update `PROTOCOL.md`**

Replace the `**OPEN — the difficulty-deciding question:**` bullet in the `vxamp` section with the resolved verdict and a link to `docs/vxamp-format.md`.

- [ ] **Step 3: Verify the docs match the code**

Run: `python -m pytest tools/vxamp-re -v`
Expected: full suite PASS — the documented layout is exactly what the passing codec implements.

- [ ] **Step 4: Commit**

```bash
git add docs/vxamp-format.md PROTOCOL.md
git commit -m "vxamp-re: document the vxamp container format + verdict"
```

---

### Task 9 (OPTIONAL, guarded on-device A/B): audible confirmation

Only meaningful if `VERDICT=="refit"` (repack is already proven by byte-exact match). Confirms a self-quantized model actually loads and sounds right. Requires the pedal attached and VoidX closed.

**Files:**
- Modify: `tools/HwCheck/Program.cs` (add a guarded `--upload-amp <vxamp> <slot>` path)

- [ ] **Step 1: Add the guarded upload path**

Add an `--upload-amp <path> <slotIndex>` branch to `tools/HwCheck/Program.cs` that: refuses unless `WritesAllowed`; backs up the target slot's current blob to `docs/backups/` first (read via `DReadBlobAsync` on `root\amp`); writes the name (chunk 0) + payload chunks 1..96 + terminator (chunk -1) via `DWriteChunkAsync`; reads the slot back and confirms byte-equality with the intended blob.

- [ ] **Step 2: Build**

Run: `dotnet build tools/HwCheck`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Upload a self-quantized model to a throwaway slot**

Run: `dotnet run --project tools/HwCheck -- --upload-amp <path-to-generated.vxamp> <emptySlotIndex>`
Expected: read-back byte-match confirmation. Then the user selects that amp on the pedal and A/B-compares against the source tone.

- [ ] **Step 4: Record + commit**

Append the audible result to `FINDINGS.md`.
```bash
git add tools/HwCheck/Program.cs tools/vxamp-re/FINDINGS.md
git commit -m "vxamp-re: guarded on-device amp upload for audible A/B"
```
