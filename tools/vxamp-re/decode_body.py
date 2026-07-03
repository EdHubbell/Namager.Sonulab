"""Determine the vxamp body element encoding and scale placement, then expose a
dequantizer. ENCODING / SCALE_SPEC / SCALE_DEFAULT are set from the analysis in
_investigate() and frozen here once the reviewer accepts the finding."""
from __future__ import annotations
import numpy as np
import vxamp as vx

# ---- Determined by Task 3 analysis (see FINDINGS.md). ----
ENCODING = "int8"          # 8224 int8 elements. See FINDINGS.md Task 3.
SCALE_SPEC = "global-constant"  # no periodic/per-block scale slots found in the body
SCALE_DEFAULT = 1.0 / 127  # starting guess; exact global scale pending Task 3E controlled capture


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
    print(f"max |CDF_even - CDF_odd| = {np.abs(np.cumsum(he) - np.cumsum(ho)).max():.4f}")


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
    print("--- even/odd offset byte distributions (int16-LE discriminator) ---")
    _even_odd_stats(bodies)


if __name__ == "__main__":
    _investigate()
