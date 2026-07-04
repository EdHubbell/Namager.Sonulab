# `root\ir` blob format — pinned 2026-07-04

**Status: PINNED (GATE 1b passed).** Derived from `tools/ir-re/analyze_irs.py` run against
Ed's four dobro IR `.wav` sources (`NAMFiles/IR/*.wav`) and their VoidX-uploaded device dumps
(`NAMFiles/IrDump/16..19 - *IRdobro*.irblob`, captured in Task 4 / GATE 1a).

## Winning hypothesis: `raw-f32`

A `root\ir` blob is **1024 little-endian IEEE-754 float32 samples** (4096 bytes, no header,
no obfuscation), representing 1024 samples of 44.1 kHz mono audio.

- `xor-f32` (vxamp keystream XOR then float32) — **rejected**: decodes to non-finite garbage
  (`finite=False`, `max|v|≈3e38`) for every dobro slot. The vxamp obfuscation scheme does not
  apply to IR blobs.
- `raw-i16` (2048 int16 LE samples) — **rejected**: near-zero correlation against every wav
  (|corr| < 0.05 for all 16 dobro (slot × wav) pairs), and the decoded samples have ~0 lag-1
  autocorrelation (`lag1≈±0.03`), i.e. it looks like noise, not audio.
- `raw-f32` — **confirmed**: each of the 4 dobro slots correlates at **corr = +1.0000** against
  its own source wav's first 1024 samples (float64 vs. float32-round-tripped, so effectively
  exact), and the sample-by-sample ratio `decoded / source_head` has std ≈ 9e-9 (i.e. the
  relationship is a single exact linear scale — no other transform).

| Device slot | Source wav | corr (own pair) | gain (least-squares wav→blob) |
|---|---|---|---|
| `16 - IRdobro0%Smoothness` | `IRdobro0%Smoothness.wav` | **+1.0000** | 0.34263 |
| `17 - _IRdobro0.3%Smoothness` | `IRdobro0.3%Smoothness.wav` | **+1.0000** | 0.35366 |
| `18 - IRdobro0.6%Smoothness` | `IRdobro0.6%Smoothness.wav` | **+1.0000** | 0.35953 |
| `19 - IRdobro0.9%Smoothness` | `IRdobro0.9%Smoothness.wav` | **+1.0000** | 0.36682 |

(Cross-pairs among the 4 similar dobro wavs also show high corr, 0.92–0.994, since the
smoothness variants are shape-similar impulse responses — but only the true own-pair hits the
exact +1.0000 that identifies the source-of-truth match. `slot 17`'s device name carries a
stray leading `_` vs. its wav stem; the all-pairs comparison in the script is name-agnostic so
this didn't hide the match.)

## Sample count

`BlobBytes / 4 = 4096 / 4 = 1024` samples for float32. Confirmed (not just assumed): the
decoded-blob length (1024) is exactly what correlates at +1.0000 against the wav; no other
length hypothesis was tested or needed.

## Truncation behavior

Each source wav is **~8000+ samples long** at 44.1 kHz (`IRdobro0%/0.3%/0.6%Smoothness.wav`:
8192 samples; `IRdobro0.9%Smoothness.wav`: 7984 samples) — a few hundred milliseconds. The
blob holds **the first 1024 samples only** (a head truncation, not a decimation/downsample):
correlation against the wav's head is already +1.0000, so no mid-file window comparison was
needed to resolve ambiguity — the head hypothesis is unambiguous and exact.

## Scaling rule: unit-L2-norm of the truncated window

The gain values above are **not** a fixed constant (0.343 → 0.367) and are **not** simply
"peak normalize to 1.0" (decoded peaks are 0.524–0.588, not 1.0). Testing normalization
conventions against the measured gain pins it exactly:

```
gain === 1 / L2norm(source_wav[:1024])          (confirmed to 1.000000, all 4 pairs)
```

i.e. **the device stores the first 1024 samples of the source IR, scaled so that those 1024
samples have unit Euclidean (L2) norm**: `sum(sample_i^2) == 1.0`. This was checked against
L2-norm-of-full-file too (`gain * l2norm_full` ≈ 1.001–1.004, close but consistently off by a
few tenths of a percent — not exact) — the **truncated-window** L2 norm is the one that hits
1.000000 exactly for every pair, confirming the normalization is computed on the 1024-sample
window that gets stored, not on the full-length capture.

`Sonulab.Distill.IrFormat.Encode`/`Decode` do **not** perform this normalization themselves —
they are a pure byte codec (matches the `IrFormat.cs` template contract). Callers that produce
new IR content for upload must normalize their 1024-sample window to unit L2 norm before
calling `Encode`, per this pinned rule.

## Task 4 cross-check: trailing-zero-trimmed slot sizes

GATE 1a (Task 4) noted some dumped slots' payload trailing-zero-trims to ~2756 B. That
observation is about *other* (non-dobro) slots in the 24-slot IrDump sample, not the 4 dobro
pairs used here — all 4 dobro blobs are full 4096 B with a non-trivial (non-zero-tail) 1024th
sample. It's consistent with this format: an IR shorter than 1024 samples (or one whose tail
decays to ~0 before sample 1024) would trailing-zero-trim under simple whitespace/zero
compression; no further hypothesis work was needed to explain it under `raw-f32`.

## Reproduce

```powershell
python tools/ir-re/analyze_irs.py
```

Reads `NAMFiles/IrDump/*.irblob` and `NAMFiles/IR/*.wav` (read-only). Full output archived in
`.superpowers/sdd/task-5-report.md`.

Pinned: 2026-07-04, by `tools/ir-re/analyze_irs.py` against Ed's 4 dobro `.wav`/`.irblob` pairs.
