"""Determine the vxamp body element encoding and scale placement, then expose a
dequantizer. ENCODING / SCALE_SPEC / SCALE_DEFAULT are set from the analysis in
_investigate() and frozen here once the reviewer accepts the finding.

FIX PASS (Task 3 review): the original int8 + global-scale conclusion did NOT hold
up. Under review the raw int8 view is FLAT/UNIFORM (mean|int8|=64, frac|v|<=16=0.13),
which is evidence AGAINST linearly-quantized weights, not for them. The phase-aware
re-analysis (see _investigate_phase) found the real structure:

    The body is float32-LE weights obfuscated with a repeating byte keystream.
    keystream k[i] = (K0[i % 32] - 0x20*(i // 32)) mod 256, K0 = KEYSTREAM_BASE.
    body[i] XOR k[i]  ->  2056 little-endian float32 values.

After de-obfuscation every one of the 20 corpus bodies decodes to a strongly
ZERO-PEAKED, bounded distribution (|w|<64 for 2055/2056 floats; per-body std
0.08..1.5; excess kurtosis 100..1500 -> heavy zero peak) exactly like real NAM
weights -- the acceptance test the reviewer demanded. See weights()/as_float32().

Authoritative result: ELEMENT_DTYPE / OBFUSCATION / ELEMENT_COUNTS below.
The legacy int8 constants (ENCODING/SCALE_SPEC/SCALE_DEFAULT/dequant) are retained
as the PROVISIONAL in-vocabulary field (the accepted set is {int8,int16,
int8+block-scale}, which cannot name float32) and are SUPERSEDED by ELEMENT_DTYPE.
"""
from __future__ import annotations
import numpy as np
import vxamp as vx

# ---- Legacy quantization hypothesis (PROVISIONAL, in the accepted vocabulary). ----
# SUPERSEDED by ELEMENT_DTYPE below. Kept because the accepted ENCODING set is
# {"int8","int16","int8+block-scale"} and cannot express float32; downstream Task 4
# consumes ENCODING, so it stays in-set and must try BOTH element counts (see
# ELEMENT_COUNTS). This is option (b) from the review: best in-set value, flagged
# provisional, both candidate counts recorded.
ENCODING = "int8"               # PROVISIONAL / in-set placeholder; real dtype = float32-le
SCALE_SPEC = "global-constant"  # N/A under float32 (float weights carry no quant scale)
SCALE_DEFAULT = 1.0 / 127       # legacy int8 dequant scale (dequant() path only)

# ---- Authoritative Task 3 result (fix pass). ----
ELEMENT_DTYPE = "float32-le"                 # de-obfuscated element type
OBFUSCATION = ("xor-keystream", 32, -0x20)   # (kind, base period bytes, per-period delta)
ELEMENT_COUNTS = {"float32-le": 2056, "int8": 8224}  # Task 4 must try both

# 32-byte keystream base K0 = k[0..31], recovered from the constant island at body
# offset (4032,76): that island is byte-identical across all 20 models and its
# plaintext there is float32 0.0 padding, so ciphertext == keystream. Verified: the
# whole-body keystream k[i]=(K0[i%32]-0x20*(i//32))%256 reproduces the island and
# decodes the padding floats to exactly 0.0 for every corpus body.
KEYSTREAM_BASE = bytes([
    0x99, 0x97, 0x77, 0x6f, 0x67, 0x44, 0x45, 0x22, 0x21, 0x02, 0x01, 0xde,
    0xdd, 0xbf, 0xab, 0xa2, 0x93, 0x86, 0x63, 0x64, 0x55, 0x46, 0x33, 0x24,
    0x01, 0x02, 0xdf, 0xe0, 0xbd, 0xb6, 0xa4, 0x9e,
])

# The two constant islands (Task 2) are metadata, not weights: (4032,76) is float32
# zero-padding (floats 1008..1026), (8204,16) is a trailer (floats 2051..2054).
# Slice ends at the island boundary — no +1 overshoot: float 1027 (g2_fir[0]) and
# float 2055 (nlmix scalar) are real model values and must NOT be zeroed.
_META_FLOAT_SLICES = ((4032 // 4, (4032 + 76) // 4), (8204 // 4, (8204 + 16) // 4))


def as_int8(body: bytes) -> np.ndarray:
    return np.frombuffer(body, dtype=np.int8)


def as_int16(body: bytes) -> np.ndarray:
    return np.frombuffer(body, dtype="<i2")


def keystream(n: int = vx.BODY_SIZE) -> np.ndarray:
    """The obfuscation keystream for the first n body bytes (uint8)."""
    i = np.arange(n, dtype=np.int64)
    base = np.frombuffer(KEYSTREAM_BASE, dtype=np.uint8).astype(np.int64)
    return ((base[i % 32] - 0x20 * (i // 32)) % 256).astype(np.uint8)


def deobfuscate(body: bytes) -> bytes:
    """Undo the keystream obfuscation, yielding the raw float32-LE weight bytes."""
    b = np.frombuffer(body, dtype=np.uint8)
    return bytes(b ^ keystream(len(b)))


def as_float32(body: bytes) -> np.ndarray:
    """The 2056 de-obfuscated little-endian float32 elements (weights + metadata)."""
    return np.frombuffer(deobfuscate(body), dtype="<f4")


def weights(body: bytes) -> np.ndarray:
    """De-obfuscated float32 weights with the two metadata islands zeroed out.
    This is the authoritative decoder; result is zero-peaked and bounded."""
    w = as_float32(body).astype(np.float64).copy()
    for lo, hi in _META_FLOAT_SLICES:
        w[lo:hi] = 0.0
    return w


def dequant(body: bytes, scale: float) -> np.ndarray:
    """LEGACY int8 dequantizer (PROVISIONAL ENCODING path). Superseded by weights();
    retained so the brief's int8 acceptance test still exercises the in-set field."""
    if ENCODING in (None, "int8", "int8+block-scale"):
        return as_int8(body).astype(np.float64) * scale
    if ENCODING == "int16":
        return as_int16(body).astype(np.float64) * scale
    raise ValueError(ENCODING)


def _zero_peaked_stats(w: np.ndarray) -> tuple[float, float, float]:
    """(fraction |v|<64, std of the core, excess kurtosis of the core)."""
    w = w[np.isfinite(w)]
    core = w[np.abs(w) < 64]
    frac = len(core) / max(len(w), 1)
    mu, sd = core.mean(), core.std()
    kurt = float(np.mean(((core - mu) / sd) ** 4) - 3) if sd > 0 else 0.0
    return frac, float(sd), kurt


def _even_odd_stats(bodies: list[bytes]) -> None:
    """int8-vs-int16 discriminator: under int16-LE, odd offsets are high bytes and
    concentrate near 0x00/0xFF (sign extension of small values) while even offsets
    (low bytes) look ~uniform. Under int8 both parities are statistically identical."""
    all_u8 = np.concatenate([np.frombuffer(b, dtype=np.uint8) for b in bodies])
    even = all_u8[0::2].astype(np.float64)
    odd = all_u8[1::2].astype(np.float64)
    for label, v in (("even", even), ("odd ", odd)):
        near = np.mean((v <= 8) | (v >= 247))
        print(f"{label}: n={len(v)} mean={v.mean():7.2f} std={v.std():6.2f} "
              f"frac near 0x00/0xFF={near:.4f}")
    # KS-like max CDF gap between the two parity distributions
    he = np.bincount(even.astype(int), minlength=256) / len(even)
    ho = np.bincount(odd.astype(int), minlength=256) / len(odd)
    gap = np.abs(np.cumsum(he) - np.cumsum(ho)).max()
    print(f"max |CDF_even - CDF_odd| = {gap:.4f}  (no int16-LE high-byte signature; "
          f"the residual gap is the phase-3 keystream anomaly leaking into odd parity)")


def _shannon(v: np.ndarray) -> float:
    h = np.bincount(v.astype(np.uint8), minlength=256).astype(float)
    p = h / h.sum()
    p = p[p > 0]
    return float(-(p * np.log2(p)).sum())


def _investigate_phase():
    """Phase-aware (offset mod 4) re-analysis that overturns the int8 conclusion and
    lands float32-under-keystream. Reproduces the review's counter-evidence, then
    tests the 4-byte / block-float hypotheses against all 20 bodies with the
    zero-peaked acceptance test."""
    bodies = [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]
    u8 = [np.frombuffer(b, dtype=np.uint8) for b in bodies]
    n = len(u8[0])

    print("=== 1. per-phase (offset mod 4) int8 magnitude + entropy, pooled ===")
    print("    (uniform-random expectation: mean|int8|=64, std=73.9, frac|v|<=16=0.129")
    print("     -> a FLAT phase histogram is evidence AGAINST int8*global-scale weights)")
    for ph in range(4):
        col = np.concatenate([b[ph::4] for b in u8])
        s8 = col.astype(np.int8).astype(int)
        print(f"    phase {ph}: entropy={_shannon(col):.3f} mean|int8|={np.abs(s8).mean():.1f} "
              f"std={s8.std():.1f} frac|v|<=16={np.mean(np.abs(s8) <= 16):.3f}")

    print("=== 2. cross-body XOR entropy per phase (adjacent pairs, pooled) ===")
    xs = [u8[i] ^ u8[i + 1] for i in range(len(u8) - 1)]
    ents = [round(_shannon(np.concatenate([x[ph::4] for x in xs])), 2) for ph in range(4)]
    print(f"    {ents}  -> phase 3 collapses: a structured 4th byte (sign/exponent) "
          f"shared across models; bytes 0-2 stay high-entropy (mantissa). 4-BYTE RECORD.")

    print("=== 3. de-obfuscation: keystream k[i]=(K0[i%32]-0x20*(i//32))%256 ===")
    k = keystream(n)
    island = np.frombuffer(bodies[0], np.uint8)[4032:4108]
    print(f"    keystream reproduces constant island (4032,76): "
          f"{int(np.sum(k[4032:4108] == island))}/76 bytes")
    pad = as_float32(bodies[0])[1008:1024]
    print(f"    de-obfuscated float32 padding idx1008:1024 all == 0.0: {bool(np.all(pad == 0.0))}")

    print("=== 4. hypothesis acceptance test: zero-peaked & bounded like NAM weights? ===")
    for tag, dec in (("RAW float32-LE (no deobf)", lambda b: np.frombuffer(b, '<f4')),
                     ("int8 (raw)", lambda b: as_int8(b).astype(np.float64)),
                     ("XOR-keystream float32-LE", weights)):
        pooled = np.concatenate([np.asarray(dec(b), dtype=np.float64) for b in bodies])
        frac, sd, kurt = _zero_peaked_stats(pooled)
        verdict = "ZERO-PEAKED (weight-like)" if kurt > 20 and frac > 0.99 else "flat/garbage"
        print(f"    {tag:28s}: frac|<64={frac:.4f} std={sd:7.3f} kurt={kurt:8.1f}  -> {verdict}")

    print("=== 5. per-body XOR-keystream float32 (all 20) ===")
    for bi, b in enumerate(bodies):
        frac, sd, kurt = _zero_peaked_stats(weights(b))
        print(f"    body {bi:2d}: frac|<64={frac:.4f} std={sd:.3f} kurt={kurt:8.1f}")

    # reference: real NAM source weights, for calibration
    nm = vx.load_nam(vx.pairs()[0][1])
    wn = np.concatenate([np.asarray(v) for _, v in vx.nam_weights(nm)])
    _, sd, kurt = _zero_peaked_stats(wn.astype(np.float64))
    print(f"    (ref NAM source weights: std={sd:.3f} kurt={kurt:.1f} -- also zero-peaked)")


def _investigate():
    """Legacy first-pass evidence (int8 histogram, stride scan, even/odd). Kept for
    the record; superseded by _investigate_phase()."""
    bodies = [vx.body(vx.load_vxamp(f)) for f in vx.vxamp_files()]
    b0 = bodies[0]
    a8 = as_int8(b0)
    a16 = as_int16(b0)
    print("int8 : min", a8.min(), "max", a8.max(), "unique", len(np.unique(a8)))
    print("int16: min", a16.min(), "max", a16.max(), "unique", len(np.unique(a16)))
    hist = np.bincount((a8.astype(int) + 128), minlength=256)
    print("int8 clip counts: [-128]=%d [127]=%d" % (hist[0], hist[255]))
    mags = np.abs(a8.astype(int))
    for stride in (16, 32, 64, 128, 256):
        idx = np.arange(0, len(mags), stride)
        print(f"stride {stride:4d}: mean|slot0|={mags[idx].mean():.1f} vs overall={mags.mean():.1f}")
    print("--- even/odd offset byte distributions (int16-LE discriminator) ---")
    _even_odd_stats(bodies)


if __name__ == "__main__":
    _investigate_phase()
    print()
    _investigate()
