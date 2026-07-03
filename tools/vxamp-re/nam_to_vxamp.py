"""vxamp container WRITER: assemble a full slot from named model-parameter tensors.

write_vxamp(tensors) -> bytes
    Build a 12288-byte vxamp slot from the SEMANTIC named float32 tensors produced
    by arch.split_body() / codec.decode()["tensors"]:

        pre_fir      (1024 float32) — short tone-shaping FIR
        g2_header    (   3 float32) — TLV chunk header bytes, reinterpreted as floats
        g2_fir       (1024 float32) — long post-FIR (cab/speaker IR)
        nlmix_header (   4 float32) — TLV chunk header bytes, reinterpreted as floats
        nlmix        (   1 float32) — nonlinear-mix scalar

    The inverse of codec.decode()["tensors"]:
        concatenate -> serialize as little-endian float32 -> XOR-obfuscate -> prepend
        32-byte constant header -> zero-pad to SLOT_SIZE (12288 B).

    Validated byte-exact against all 20 corpus models (see test_nam_to_vxamp.py).

nam_to_vxamp(nam_path) -> bytes
    Raises NotImplementedError.  Converting a source .nam (WaveNet / SlimmableContainer)
    to a vxamp container requires fitting VoidX's internal FIR-cascade model — a
    Wiener-Hammerstein distillation into pre_fir + g2_fir + nlmix — which is sub-project
    2 and is NOT implemented here.  See docs/vxamp-format.md and FINDINGS.md (Task 6
    refit verdict) for the full explanation and the path forward.

Scope note (POST-TASK-4 ARCHITECTURE + VERDICT):
    A byte-exact .nam -> .vxamp conversion IS NOT ACHIEVABLE without reproducing
    VoidX's proprietary FIR/nonlinearity fitting workflow.  write_vxamp() covers the
    container authoring half (from already-fitted parameters); the fitting itself is
    deferred to sub-project 2.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

import arch
import decode_body as db
import vxamp as vx


def write_vxamp(tensors: dict) -> bytes:
    """Assemble a 12288-byte vxamp slot from named float32 model-parameter tensors.

    Parameters
    ----------
    tensors : dict[str, np.ndarray]
        Must have exactly the keys and element counts given by arch.tensor_sizes():
        pre_fir (1024), g2_header (3), g2_fir (1024), nlmix_header (4), nlmix (1).
        Typically obtained from codec.decode(slot)["tensors"] or arch.split_body().

    Returns
    -------
    bytes
        A valid 12288-byte vxamp slot (constant header + obfuscated body + zero pad).

    Raises
    ------
    ValueError
        If *tensors* contains unexpected keys, is missing required keys, or any
        tensor has the wrong number of elements.
    """
    sizes = arch.tensor_sizes()          # [(name, n), ...] in canonical order
    required = {name: n for name, n in sizes}
    provided = set(tensors.keys())
    expected = set(required.keys())

    # Check for unexpected keys
    extra = provided - expected
    if extra:
        raise ValueError(
            f"Unexpected tensor key(s): {sorted(extra)}. "
            f"write_vxamp() accepts only: {sorted(expected)}"
        )

    # Check for missing keys
    missing = expected - provided
    if missing:
        raise ValueError(
            f"Missing required tensor(s): {sorted(missing)}. "
            f"Required keys: {sorted(expected)}"
        )

    # Validate sizes and concatenate in canonical order
    parts: list[np.ndarray] = []
    for name, n in sizes:
        arr = np.asarray(tensors[name], dtype="<f4")
        if arr.size != n:
            raise ValueError(
                f"Tensor '{name}': expected {n} elements, got {arr.size}."
            )
        parts.append(arr.reshape(-1))

    # Serialize to little-endian float32 bytes — this is the de-obfuscated body
    body_bytes: bytes = np.concatenate(parts).astype("<f4").tobytes()
    assert len(body_bytes) == vx.BODY_SIZE, (
        f"Internal error: body is {len(body_bytes)} B, expected {vx.BODY_SIZE}"
    )

    # Obfuscate with the XOR keystream (same operation reverses it)
    ks = db.keystream(len(body_bytes))
    body_obf = bytes(np.frombuffer(body_bytes, dtype=np.uint8) ^ ks)

    # Prepend constant 32-byte header, zero-pad to full slot size
    header = bytes.fromhex(vx.HEADER_HEX)
    slot = header + body_obf
    slot += b"\x00" * (vx.SLOT_SIZE - len(slot))
    return slot


def nam_to_vxamp(nam_path) -> bytes:  # noqa: ANN001
    """NOT IMPLEMENTED — .nam to vxamp distillation is sub-project 2.

    Converting a source .nam model (WaveNet or SlimmableContainer architecture)
    into a Sonulab vxamp container is NOT a simple weight repack.  VoidX-Control
    *distills* the source WaveNet into a Wiener-Hammerstein FIR-cascade model
    (pre_fir + g2_fir + nlmix), which is an entirely different model class:

        Source .nam  : 1871–13802 WaveNet/SlimmableContainer float32 weights
        Device body  : 1024-tap pre_fir + 1024-tap g2_fir + 1 nlmix scalar

    There is no bijective mapping from NAM weight tensors to device tensors —
    the conversion requires fitting both FIRs and the nonlinear scalar to the
    source model's audio behaviour.  This fitting is sub-project 2 and has not
    been implemented here.

    For the full refit verdict and weight-space evidence, see:
      - FINDINGS.md  §§ Task 4 (architecture) and Task 6 (refit verdict)
      - docs/vxamp-format.md  (format overview and path forward)

    To author a vxamp container from already-fitted parameters, use write_vxamp().

    Raises
    ------
    NotImplementedError
        Always.  This function is a documented scope boundary, not an unfinished stub.
    """
    _ = Path(nam_path)   # validate the argument is at least path-like
    raise NotImplementedError(
        "nam_to_vxamp() is not implemented: converting a .nam model to vxamp requires "
        "fitting VoidX's internal FIR-cascade (Wiener-Hammerstein) model — "
        "a sub-project 2 task that is out of scope here.  "
        "See FINDINGS.md Task 6 (refit verdict) and docs/vxamp-format.md for details.  "
        "To write a container from already-fitted tensors, use write_vxamp()."
    )
