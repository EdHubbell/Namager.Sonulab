# Task 7 Report — vxamp container writer + .nam scope boundary

## What was built

### `tools/vxamp-re/nam_to_vxamp.py`

Two public functions:

**`write_vxamp(tensors: dict) -> bytes`**
- Accepts the same dict shape that `arch.split_body()` / `codec.decode()["tensors"]` returns:
  `pre_fir` (1024), `g2_header` (3), `g2_fir` (1024), `nlmix_header` (4), `nlmix` (1).
- Validates the dict: raises `ValueError` for any unexpected key, any missing key, or any
  tensor with the wrong element count (error message names the offending key in all cases).
- Concatenates the tensors in `arch.tensor_sizes()` order into a 2056-float32 array,
  serializes as little-endian float32 bytes (8224 B = de-obfuscated body).
- Obfuscates: `body_obf = body_bytes XOR decode_body.keystream(8224)`.
- Prepends the 32-byte constant header (`bytes.fromhex(vx.HEADER_HEX)`), zero-pads to
  `vx.SLOT_SIZE` (12288 B).
- This is the meaningful new capability: authoring the container directly from semantic model
  parameters — distinct from `codec.encode()` which only echoes an opaque `raw_body` blob.

**`nam_to_vxamp(nam_path) -> bytes`**
- Raises `NotImplementedError` unconditionally with a detailed scope message.
- Message explains the refit verdict, the model-class incompatibility (WaveNet ≠ FIR-cascade),
  and points the reader to `FINDINGS.md` Task 6 and `docs/vxamp-format.md`.
- Does NOT fake a byte-exact converter; does NOT silently return garbage bytes.

### `tools/vxamp-re/test_nam_to_vxamp.py`

Four tests (TDD RED→GREEN):

| Test | What it checks |
|------|---------------|
| `test_write_vxamp_roundtrips_every_real_model_byte_exact` | `write_vxamp(decode(slot)["tensors"]) == slot` for all 20 corpus models |
| `test_write_vxamp_produces_valid_container_shape` | `len == SLOT_SIZE`, `header.hex() == HEADER_HEX`, `size_field == PAYLOAD_SIZE` |
| `test_write_vxamp_rejects_wrong_tensor_dict` | ValueError for missing key, extra key, wrong-size tensor — all three branches |
| `test_nam_to_vxamp_is_scoped_out` | `NotImplementedError` for Path and str inputs |

## Byte-exact-from-tensors result

`write_vxamp(codec.decode(slot)["tensors"]) == slot` passes for **all 20/20 corpus models**
in `test_write_vxamp_roundtrips_every_real_model_byte_exact`.

This confirms:
1. The `tensors` dict from `codec.decode()` fully captures the model's parameters.
2. The concatenate→serialize→XOR→prepend-header pipeline is the exact inverse of the
   decode path (`arch.split_body` → de-obfuscate → as_float32 → slice).
3. Zero-padding is reproduced correctly (it comes from the file, not generated).

## Scoped-out stub

`nam_to_vxamp()` is a documented scope boundary, not a placeholder. The refit verdict
(Task 6, FINDINGS.md) is authoritative: there is no weight-level mapping from a source
`.nam` to a device vxamp body.  The fitting sub-project (extract WaveNet IR → fit
pre_fir + g2_fir + nlmix via least-squares / IFFT) is sub-project 2 and is explicitly
out of scope for this harness.

## TDD RED→GREEN

- **RED**: 4 failures (`ModuleNotFoundError: No module named 'nam_to_vxamp'`) before
  `nam_to_vxamp.py` existed.
- **GREEN**: 4/4 pass after implementation.  Full suite: **33/33 passed**.

## Concerns / notes

- `g2_header` and `nlmix_header` are included as tensors (3 and 4 float32 elements) even
  though their content is fixed TLV metadata, not model parameters.  This keeps the
  round-trip byte-exact without special-casing; a future API could accept only the true
  model parameters (`pre_fir`, `g2_fir`, `nlmix`) and regenerate the chunk headers from
  `DEVICE_ARCH` constants — but that's a sub-project 2 concern.
- The `nam_to_vxamp()` scope stub accepts both `Path` and `str` (it only calls
  `Path(nam_path)` for argument validation, then raises).  Both forms are tested.
- No `__pycache__` was created (pytest ran but those dirs are gitignored).

## Files committed

- `tools/vxamp-re/nam_to_vxamp.py`
- `tools/vxamp-re/test_nam_to_vxamp.py`
- `.superpowers/sdd/task-7-report.md` (this file)
