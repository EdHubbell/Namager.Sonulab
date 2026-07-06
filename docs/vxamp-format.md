# vxamp container format — reference

Reverse-engineered from a corpus of 20 live `root\amp` slots dumped via
`HwCheck --dump-amps` on 2026-07-03, paired against their source `.nam` files
in `NAMFiles/`. Tasks 1–6 in `tools/vxamp-re/FINDINGS.md` are the authoritative
evidence record; this document is the consolidated reference.

---

## 1. Container at a glance

| Region | Offset (bytes) | Length | Contents |
|--------|---------------|--------|----------|
| Header | 0 | 32 | Constant across all models |
| Body | 32 | 8224 | XOR-obfuscated float32-LE weights / model tensors |
| Padding | 8256 | 4032 | Zero bytes in VoidX-written slots. StompStation Manager stores its SSMD metadata block here (see src/Sonulab.Distill/VxampMetadata.cs); firmware ignores the region (validation pending — docs/HARDWARE-VALIDATION-amp-metadata.md). |

- **Slot size:** 12288 B (`root\amp` slot; `size 12288`, `chunk 128`, 96 chunks).
- **Payload size:** 8256 B (header + body).  The size field in the header encodes this.
- **Body size:** 8224 B = payload − header = 2056 float32 elements once de-obfuscated.
- Every occupied slot — regardless of source `.nam` architecture — has this identical
  layout.  VoidX-Control maps any NAM into the fixed device model class before upload.

---

## 2. Header (bytes 0–31, constant)

Hex: `4020000000000000416d70206d6f64656c000000797653442122ff009ae7c4be`

| Bytes | Hex | Interpretation |
|-------|-----|----------------|
| 0–1 | `40 20` | u16 LE = **0x2040 = 8256** — payload size field |
| 2–7 | `00 00 00 00 00 00` | zeros (reserved) |
| 8–19 | `41 6d 70 20 6d 6f 64 65 6c 00 00 00` | ASCII `"Amp model"` + 3 NUL bytes |
| 20–23 | `79 76 53 44` | ASCII tag `"yvSD"` |
| 24–31 | `21 22 ff 00 9a e7 c4 be` | constant config bytes (meaning TBD) |

All 32 bytes are identical for every model in the 20-model corpus (`test_vxamp.py::test_header_is_constant_across_all_models`).

---

## 3. Body encoding — float32-LE + XOR keystream obfuscation

### 3.1 Encoding

The 8224-byte body encodes **2056 little-endian float32 values**, obfuscated by a
position-dependent byte XOR:

```
k[i] = (K0[i % 32]  −  0x20 · (i // 32))  mod 256      (all arithmetic mod 256)
plaintext_byte[i] = body_byte[i] XOR k[i]
float_array = np.frombuffer(plaintext_bytes, dtype="<f4")   # 2056 elements
```

`K0` is the 32-byte **KEYSTREAM_BASE** (below).  The keystream decrements by `0x20`
every 32 bytes (a linear ramp on the base), wrapping mod 256.

### 3.2 KEYSTREAM_BASE (K0)

```python
KEYSTREAM_BASE = bytes([
    0x99, 0x97, 0x77, 0x6f, 0x67, 0x44, 0x45, 0x22,
    0x21, 0x02, 0x01, 0xde, 0xdd, 0xbf, 0xab, 0xa2,
    0x93, 0x86, 0x63, 0x64, 0x55, 0x46, 0x33, 0x24,
    0x01, 0x02, 0xdf, 0xe0, 0xbd, 0xb6, 0xa4, 0x9e,
])
```

### 3.3 How the keystream was recovered (known-plaintext attack)

Task 2 found a 76-byte constant island at body offset 4032 — byte-identical across
all 20 models.  Its de-obfuscated plaintext is float32 `0.0` padding (the zero tail
of `pre_fir`; see section 4), so `ciphertext == keystream` there.  From those 76 bytes
the 32-byte base and the `−0x20`-per-32-byte ramp were extracted and verified:

- Keystream reproduces the island: **64/64 bytes** match (the remaining 12 bytes of
  the 76-byte island are a chunk header, not zero padding).
- De-obfuscating that region gives exactly float32 `0.0` for every corpus body ✓.

### 3.4 Validation — decoded weights are zero-peaked and bounded

```
Decode strategy                frac|v|<64   std     excess kurtosis
RAW float32-LE (no de-obf)     0.517        6.09    66      ← garbage (~50% ±1e38)
int8 raw                       0.498       36.9     −1      ← FLAT/uniform (not weight-like)
XOR-keystream → float32-LE     1.000        0.49  3824      ← ZERO-PEAKED ✓
```

All 20 bodies individually: `frac|v|<64 = 1.000`, per-body std 0.08–1.47, excess
kurtosis 100–1560.  Reference NAM source weights have std 0.117, kurtosis 67 — also
zero-peaked.  De-obfuscation is thus confirmed correct.

**int16-LE was also ruled out** (no high-byte pile-up at 0x00/0xFF: max CDF gap 0.0234
is the phase-3 keystream/exponent artefact, not an int16 high-byte signature).

---

## 4. Model structure — Wiener–Hammerstein FIR-cascade in a TLV container

The device does **not** run a WaveNet.  The 2056 de-obfuscated floats are a compact
**FIR-cascade (Wiener–Hammerstein-style)** DSP model — tone-shaping pre-filter,
nonlinear stage, cab/speaker post-filter — stored as a hybrid of a raw section plus a
TLV chunk chain.

### 4.1 Byte-level layout (de-obfuscated body, all 20 models)

```
Body (8224 bytes, de-obfuscated)
├── [0,    4096)  pre_fir raw section
│                 1024 float32 FIR taps; taps 1008..1023 = 0.0 in every corpus model
│                 (windowed/fade tail; Task 2 constant island (4032,76) starts here)
│
├── [4096, 8204)  chunk "G2"  (4108 bytes)
│   ├── header 12 B: u32 len=0x100C | u32 0 | char[4] "G2\0\0"
│   └── payload 4096 B: 1024 float32 = g2_fir (cab/speaker IR)
│
└── [8204, 8224)  chunk "nlmix"  (20 bytes)
    ├── header 16 B: u32 len=0x14 | u32 0 | char[8] "nlmix\0\0\0"
    └── payload 4 B: 1 float32 = nlmix scalar
```

Chain arithmetic: `4096 + 0x100C + 0x14 = 8224` — exact match to body size for
**all 20 models** (`arch.parse_chunks` validates this; test-enforced).

### 4.2 Tensor table

| Name | Float32 elements | Body bytes | Role |
|------|-----------------|------------|------|
| `pre_fir` | 1024 | [0, 4096) | Short tone-shaping FIR; ~100% of energy in first ~50 taps (near-delta) |
| `g2_header` | 3 | [4096, 4108) | TLV header bytes interpreted as 3 floats; constant |
| `g2_fir` | 1024 | [4108, 8204) | Long post-FIR (cab/speaker IR); decaying envelope |
| `nlmix_header` | 4 | [8204, 8220) | TLV header bytes interpreted as 4 floats; constant |
| `nlmix` | 1 | [8220, 8224) | Nonlinear-mix scalar; range 0..0.67 in corpus; exactly 0.0 for 7 (clean) models |

Reconciliation: **1024 + 3 + 1024 + 4 + 1 = 2056** float32 elements = 8224 B ✓.

The two constant islands from Task 2 are explained exactly:
- `(4032, 76)` = `pre_fir` zero tail + start of the G2 chunk header.
- `(8204, 16)` = `nlmix` chunk header.

### 4.3 Evidence that the sections are FIR curves, not NN weights

- **Smoothness:** lag-1 autocorrelation of `g2_fir` = 0.32–0.95; `pre_fir` up to 0.85.
  Real NAM WaveNet weight vectors measure ≈0.16 (iid-like).  Both sections have
  decaying |v| envelopes characteristic of impulse responses.
- **Spectral cascade match:** a numpy re-implementation of the VoidX-fork WaveNet
  forward gives each source model's small-signal impulse response (self-consistency
  check: corr(IR_sub0, IR_sub1) = 0.966).  Log-magnitude spectrum correlation,
  60 Hz–12 kHz, 11 SlimmableContainer pairs:

  ```
  pre_fir ⊛ g2_fir   median corr 0.915   ← cascade wins
  g2_fir alone        median corr 0.845
  pre_fir alone       median corr 0.734
  ```

  The two FIRs in **series** jointly carry the model's linear response.  (One outlier:
  Roland JC-120 chorus amp, corr 0.35 — expected for a time-varying amp.)

### 4.4 Confirmed vs provisional

**Confirmed (exact, all 20 bodies):**
- Section boundaries and element counts (TLV arithmetic closes exactly).
- Element encoding: float32-LE after XOR-keystream de-obfuscation.
- `nlmix` is a scalar gating a nonlinear stage (0 for clean models, up to 0.67 for
  drive models).
- The two FIRs are in series and jointly carry the linear response.

**Provisional (corpus-only inference; a controlled before/after capture would settle):**
- Exact runtime topology: precisely where the nonlinearity sits between the FIRs and
  what shape it takes.
- Whether `g2_fir[0]` is the first filter tap or a "G2" drive/level scalar parameter.
- Tag semantics: "G2" is guessed (gain / stage-2); "nlmix" is near-certain from the
  ASCII name and the 0-for-clean-models pattern.

---

## 5. Verdict — REFIT (not repack)

**`VERDICT = "refit"`**: VoidX-Control *distills* the source NAM (WaveNet or
SlimmableContainer) into a wholly different model class.  The device body cannot be
reproduced from the `.nam` without re-running VoidX's internal FIR/nonlinearity fitting.

### 5.1 Model-class argument

| Aspect | Source `.nam` | Device body |
|--------|--------------|-------------|
| Architecture | WaveNet (dilated conv + LeakyReLU + residual) or SlimmableContainer | FIR-cascade: `pre_fir` (1024 taps) + `g2_fir` (1024 taps) + `nlmix` scalar |
| Weight count | 1871 (Slim sub0), 12146 (Slim sub1), or 13802 (WaveNet) | 2056 float32 (2048 filter taps + 8 metadata/scalar) |
| Shared dimensions | None | — |

### 5.2 Per-pair weight-space evidence (all 14 corpus pairs)

`exact_frac` = fraction of source weights appearing verbatim (within 1e-4) as a
contiguous run in the de-obfuscated float stream.  `corr` = Pearson correlation,
device weight vector vs best-size source submodel.

```
Pair                               exact_frac      corr  max_abs_err
--------------------------------  ----------  --------  -----------
Bassman 5F6A - Super Clean          0.005208    0.0194       8.71
Princeton EOB 5 M160                0.000145    0.0174      52.47
Blackface Twin Reverb 65 2x12       0.002996    0.0846       2.64
Deluxe Reverb Clean Full            0.001355   -0.0392       8.42
Dumble Steel SS Clean Full          0.001498    0.2061       2.38
Dumble Steel SS Drive Full          0.001355    0.0539       8.19
Pano-Verb                           0.001355    0.0699       4.02
Princeton Clean 3 SM57              0.000145   -0.0439      17.22
Quad Reverb Randall Head SM57       0.000145    0.0165      25.28
Roland JC-120 Jazz Chorus           0.001641    0.0250       5.50
Super Reverb EQ Flat SM 57          0.001498   -0.0257       4.34
Twin Reverb SM57                    0.001355   -0.0610       3.64
Vibrolux Reverb                     0.001355   -0.1321       8.51
Vox AC30 Clean                      0.000499   -0.0903      24.12

Summary: exact_frac max = 0.005208, all < 0.01
         corr range −0.13 … +0.21  (noise; no pair |corr| > 0.21)
         max_abs_err range 2.4 … 52.5  (large across the board)
```

- `exact_frac < 0.006` on every pair.  Residual non-zero values are incidental
  collisions in the near-zero tails of both weight and FIR distributions — not evidence
  of a repack.
- `corr ≈ 0` — no linear relationship; not a scaling/reordering of source weights.
- Combined with the spectral cascade corr 0.915 (section 4.3), the picture is
  consistent with a **fitted approximation** of the source's linear response, not a copy.

### 5.3 Consequence

Byte-exact `.nam` → `.vxamp` conversion is **NOT achievable** without reproducing
VoidX's proprietary FIR/nonlinearity fitting.  The path forward is **sub-project 2**:
fit our own FIR-cascade into the now-fully-understood container.

Steps already validated in numpy:
1. Extract the source `.nam`'s small-signal impulse response via the VoidX-fork WaveNet
   forward (already implemented in `tools/vxamp-re/`; spectral self-consistency 0.966).
2. Fit `pre_fir` + `g2_fir` to that IR (least-squares Wiener filter or IFFT).
3. Determine the `nlmix` scalar from the source's nonlinear behaviour (or default `0.0`
   for clean models).
4. Assemble using `write_vxamp()` (step 4 is already working; see section 6.3).

---

## 6. Tooling

All tools live in `tools/vxamp-re/`.  The corpus was dumped with
`dotnet run --project tools/HwCheck -- --dump-amps` → `NAMFiles/VxampDump/*.vxamp`.

### 6.1 Decode a slot

```python
import vxamp as vx
import codec

slot = vx.load_vxamp("NAMFiles/VxampDump/01 - Bassman 5F6A - Super Clean.vxamp")
d = codec.decode(slot)
# d["header"]   — 32 constant bytes
# d["raw_body"] — 8224 de-obfuscated float32-LE bytes
# d["tensors"]  — dict: pre_fir, g2_header, g2_fir, nlmix_header, nlmix (numpy arrays)
# d["chunks"]   — [("G2", 0x100C), ("nlmix", 0x14)]
```

### 6.2 Encode / round-trip

```python
slot2 = codec.encode(d)          # byte-exact reproduction
assert codec.roundtrip_ok(slot)  # True for all 20 corpus models
```

### 6.3 Author a new slot from tensors

```python
from nam_to_vxamp import write_vxamp
import numpy as np

tensors = {
    "pre_fir":      np.zeros(1024, dtype="<f4"),  # replace with fitted taps
    "g2_header":    d["tensors"]["g2_header"],     # copy from any real slot (constant)
    "g2_fir":       np.zeros(1024, dtype="<f4"),  # replace with fitted cab IR
    "nlmix_header": d["tensors"]["nlmix_header"],  # copy from any real slot (constant)
    "nlmix":        np.array([0.0], dtype="<f4"), # 0.0 = fully linear
}
new_slot = write_vxamp(tensors)  # 12288-byte valid vxamp slot
```

### 6.4 Worked example — slot to tensors

```python
import vxamp as vx
import decode_body as db
import arch

# Step 1: load the 12288-byte slot
slot = vx.load_vxamp("NAMFiles/VxampDump/01 - Bassman 5F6A - Super Clean.vxamp")

# Step 2: extract and de-obfuscate the body (bytes 32..8255)
body_obf = vx.body(slot)                  # 8224 B, still XOR'd
raw_body = db.deobfuscate(body_obf)       # 8224 B plaintext float32-LE bytes
floats   = db.as_float32(body_obf)        # numpy array of 2056 float32 elements

# Step 3: split into named tensors
tensors = arch.split_body(body_obf)
# tensors["pre_fir"]  — shape (1024,), short tone-shaping FIR
# tensors["g2_fir"]   — shape (1024,), cab/speaker IR
# tensors["nlmix"]    — shape (1,),    nonlinear-mix scalar

# Step 4: inspect the TLV chain
for tag, ln, payload in arch.parse_chunks(body_obf):
    print(f"chunk {tag!r}: declared_len=0x{ln:04X}, payload {len(payload)} floats")
# chunk 'G2': declared_len=0x100C, payload 1024 floats
# chunk 'nlmix': declared_len=0x0014, payload 1 floats

# Step 5: weights() masks the two metadata islands for clean analysis
weights = db.weights(body_obf)   # 2056 float32; island floats zeroed out
```

---

## 7. Source files

| File | Purpose |
|------|---------|
| `tools/vxamp-re/vxamp.py` | Container constants (`SLOT_SIZE`, `HEADER_HEX`, …) + corpus loaders |
| `tools/vxamp-re/decode_body.py` | Keystream generation, de-obfuscation, float32 decode (`KEYSTREAM_BASE`, `keystream()`, `deobfuscate()`, `as_float32()`, `weights()`) |
| `tools/vxamp-re/arch.py` | FIR-cascade TLV layout (`DEVICE_ARCH`, `tensor_sizes()`, `split_body()`, `parse_chunks()`) |
| `tools/vxamp-re/codec.py` | Round-trip decoder/encoder (`decode()`, `encode()`, `roundtrip_ok()`) |
| `tools/vxamp-re/nam_to_vxamp.py` | Container writer (`write_vxamp()`); `nam_to_vxamp()` raises `NotImplementedError` (sub-project 2) |
| `tools/vxamp-re/verdict.py` | Refit verdict constants + per-pair comparison (`VERDICT`, `compare()`) |
| `tools/vxamp-re/FINDINGS.md` | Authoritative running evidence record (Tasks 1–6) |
