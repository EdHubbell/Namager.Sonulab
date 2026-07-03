"""Device model architecture + tensor layout for the vxamp body (Task 4, DISCOVERY).

HEADLINE (PROVISIONAL, corpus-only evidence): the device does NOT store or run a
WaveNet. The 2056 de-obfuscated float32 elements (Task 3) are a **FIR-cascade
("Wiener-Hammerstein"-style) model**: a short pre-filter, a nonlinear stage with
scalar parameters, and a long post-filter (cab/speaker IR). VoidX-Control
*re-derives* this compact DSP model from the source .nam WaveNet; the NAM weights
themselves never appear in the body (they cannot: sources have 1871..13802 weights,
the body holds 2048 filter taps + 8 metadata/scalar floats).

Byte-level layout of the 8224-B body after de-obfuscation (all 20 corpus models):

  [0,    4096)  raw section: 1024 float32 FIR taps ("pre_fir").
                Taps 1008..1023 are 0.0 in every corpus model (fade/window tail;
                this is Task 2's constant island (4032,76) = zeros + next header).
  [4096, 8204)  chunk "G2":    {u32 len=0x100C; u32 0; char tag[4]="G2\\0\\0";
                               payload 4096 B = 1024 float32} ("g2_fir").
  [8204, 8224)  chunk "nlmix": {u32 len=0x14;   u32 0; char tag[8]="nlmix\\0\\0\\0";
                               payload 4 B = 1 float32} ("nlmix").

The chunk chain is exact: 4096 + 0x100C + 0x14 == 8224 for every body (len fields
cover header+payload; tags are NUL-terminated, padded to a 4-byte multiple).

Evidence for the section roles (see FINDINGS.md ## Task 4 for the numbers):
- Both sections are smooth, decaying curves (lag-1 autocorr up to 0.95), utterly
  unlike NN weight vectors (source-NAM weights: lag-1 = 0.16).
- "pre_fir" is near-delta: ~100% of its energy in the first ~50 taps -> a short
  tone-shaping filter. "g2_fir" has a long decaying tail -> a cab/speaker IR.
- A numpy re-implementation of the source (VoidX-fork) WaveNet forward gives each
  pair's small-signal impulse response; its log-magnitude spectrum matches the
  cascade pre_fir (*) g2_fir best (median corr 0.915 over 11 Slimmable pairs,
  vs 0.845 g2_fir alone, 0.734 pre_fir alone) -> the two FIRs are in SERIES and
  together carry the model's linear response.
- "nlmix" (nonlinear mix) is a scalar in [0, 0.67] across the corpus; exactly 0
  for several clean models -> blend/amount of the nonlinear stage.

PROVISIONAL parts (a controlled capture, sub-project 2 / Task 3E-style, would
settle them): the exact runtime topology (where the nonlinearity sits between the
FIRs and what shape it is), and whether g2_fir[0] is really the first tap or a
scalar drive/level parameter for the "G2" stage ("gain 2"?). The tensor BOUNDARIES
below are not provisional -- they follow from the exact TLV chunk arithmetic.
"""
from __future__ import annotations

import struct

import numpy as np

import decode_body as db

# The fixed device model. NOT a WaveNet -- recorded per the discovery-task rule
# with the WaveNet fields explicitly nulled so downstream tasks cannot mistake it.
DEVICE_ARCH = {
    "family": "fir-cascade",  # Wiener-Hammerstein-style: FIR -> NL -> FIR
    "status": "PROVISIONAL",  # roles/topology provisional; boundaries exact
    "wavenet": None,          # the device stores no WaveNet layers/channels/dilations
    "element_count": 2056,    # float32-LE elements (Task 3)
    "sections": [
        # (name, element_count, role)
        ("pre_fir", 1024, "short tone-shaping FIR; taps 1008..1023 zero in corpus"),
        ("g2_header", 3, "TLV header: u32 len=0x100C, u32 0, tag 'G2'"),
        ("g2_fir", 1024, "long post FIR (cab/speaker IR); [0] may be a drive scalar"),
        ("nlmix_header", 4, "TLV header: u32 len=0x14, u32 0, tag 'nlmix'"),
        ("nlmix", 1, "nonlinear-mix scalar, 0..~0.7 in corpus (0 = fully linear)"),
    ],
    "chunks": [("G2", 0x100C), ("nlmix", 0x14)],  # (tag, len) at 4096 and 8204
}


def tensor_sizes() -> list[tuple[str, int]]:
    """Ordered (name, element_count); sums to the 2056 float32 body elements."""
    return [(name, n) for name, n, _ in DEVICE_ARCH["sections"]]


def split_body(body: bytes) -> dict[str, np.ndarray]:
    """Slice the de-obfuscated float32 body into the named tensors, in order."""
    f = db.as_float32(body)
    out: dict[str, np.ndarray] = {}
    i = 0
    for name, n in tensor_sizes():
        out[name] = f[i:i + n]
        i += n
    if i != len(f):
        raise ValueError(f"tensor sizes cover {i} of {len(f)} elements")
    return out


def parse_chunks(body: bytes) -> list[tuple[str, int, np.ndarray]]:
    """Parse the TLV chunk chain that follows the raw pre_fir section.

    Returns [(tag, declared_len, payload_float32)], validating that the chain
    lands exactly on the body end. Chunk format: {u32 len (header+payload);
    u32 reserved==0; NUL-terminated tag padded to 4 B; float32 payload}.
    """
    d = db.deobfuscate(body)
    off = 4096
    chunks: list[tuple[str, int, np.ndarray]] = []
    while off < len(d):
        ln, reserved = struct.unpack_from("<II", d, off)
        if reserved != 0 or ln < 12 or off + ln > len(d):
            raise ValueError(f"bad chunk header at {off}: len={ln} reserved={reserved}")
        tag_end = d.index(b"\x00", off + 8)
        tag = d[off + 8:tag_end].decode("ascii")
        payload_off = off + 8 + ((tag_end - (off + 8)) // 4 + 1) * 4
        payload = np.frombuffer(d[payload_off:off + ln], dtype="<f4")
        chunks.append((tag, ln, payload))
        off += ln
    if off != len(d):
        raise ValueError(f"chunk chain ended at {off}, body is {len(d)} B")
    return chunks
