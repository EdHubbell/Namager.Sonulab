"""Pin the root\\ir blob format from dumped device blobs + their source .wav files.

Usage (repo root): python tools/ir-re/analyze_irs.py
Reads NAMFiles/IrDump/*.irblob and NAMFiles/IR/*.wav. READ-ONLY.

Hypotheses tested per blob, in order:
  raw-f32   : 1024 float32 LE samples
  xor-f32   : XOR keystream (vxamp scheme, tools/vxamp-re/decode_body.py) then float32
  raw-i16   : 2048 int16 LE samples
For each source .wav whose stem matches a dumped slot name (or for every wav x blob pair
if no names match), the wav is mono-ized, resampled to 44100 Hz if needed, and compared
against the decoded blob over the blob's full length under least-squares gain:
  corr      : Pearson correlation (shape match; the verdict metric)
  gain      : best-fit scale wav->blob (the scaling rule to bake)
A hypothesis WINS when corr > 0.99 for every available pair.
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
import decode_body as db  # keystream

ROOT = Path(__file__).resolve().parents[2]
DUMPS = sorted((ROOT / "NAMFiles" / "IrDump").glob("*.irblob"))
WAVS = sorted((ROOT / "NAMFiles" / "IR").glob("*.wav"))


def read_wav_mono_44k1(path: Path) -> np.ndarray:
    # NOTE: deviates from the brief's stdlib-`wave` reader — Ed's source .wavs are
    # IEEE-float (fmt tag 3), which the `wave` module's fmt-chunk parser rejects
    # ("unknown format: 3"). scipy.io.wavfile.read handles PCM16/24/32 and float32/64.
    sr, raw = wavfile.read(str(path))
    if np.issubdtype(raw.dtype, np.floating):
        x = raw.astype(np.float64)
    elif raw.dtype == np.int16:
        x = raw.astype(np.float64) / 32768.0
    elif raw.dtype == np.int32:
        x = raw.astype(np.float64) / 2147483648.0
    elif raw.dtype == np.uint8:
        x = (raw.astype(np.float64) - 128.0) / 128.0
    else:
        raise ValueError(f"{path.name}: unsupported sample dtype {raw.dtype}")
    if x.ndim > 1:
        x = x.mean(axis=1)
    if sr != 44100:
        x = resample_poly(x, 44100, sr)
    return x


def decode(blob: bytes, hypo: str) -> np.ndarray:
    if hypo == "raw-f32":
        return np.frombuffer(blob, dtype="<f4").astype(np.float64)
    if hypo == "xor-f32":
        b = np.frombuffer(blob, dtype=np.uint8) ^ db.keystream(len(blob))
        return np.frombuffer(bytes(b), dtype="<f4").astype(np.float64)
    if hypo == "raw-i16":
        return np.frombuffer(blob, dtype="<i2").astype(np.float64) / 32768.0
    raise ValueError(hypo)


def sanity(v: np.ndarray) -> str:
    finite = np.isfinite(v).all()
    return (f"finite={finite} max|v|={np.abs(v[np.isfinite(v)]).max() if finite or np.isfinite(v).any() else float('nan'):.4g} "
            f"lag1={np.corrcoef(v[:-1], v[1:])[0, 1] if finite and v.std() > 0 else float('nan'):+.3f}")


def compare(wav: np.ndarray, dec: np.ndarray) -> tuple[float, float]:
    n = min(len(wav), len(dec))
    a, b = wav[:n], dec[:n]
    if a.std() < 1e-12 or b.std() < 1e-12:
        return 0.0, 0.0
    corr = float(np.corrcoef(a, b)[0, 1])
    gain = float(np.dot(a, b) / max(np.dot(a, a), 1e-30))     # least-squares wav->blob
    return corr, gain


def main() -> None:
    print(f"{len(DUMPS)} dumps, {len(WAVS)} source wavs\n")
    for d in DUMPS:
        blob = d.read_bytes()
        print(f"== {d.name} ({len(blob)} B) head={blob[:16].hex()}")
        for hypo in ("raw-f32", "xor-f32", "raw-i16"):
            dec = decode(blob, hypo)
            print(f"   [{hypo}] {sanity(dec)}")
            for wpath in WAVS:
                corr, gain = compare(read_wav_mono_44k1(wpath), dec)
                mark = "  <== MATCH" if abs(corr) > 0.99 else ""
                print(f"      vs {wpath.name[:40]:40s} corr={corr:+.4f} gain={gain:+.5g}{mark}")
        print()


if __name__ == "__main__":
    main()
