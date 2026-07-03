"""Byte-exact round-trip decoder/encoder for the Sonulab vxamp container (Task 5).

Slot layout (12288 bytes):
  [0,  32)    constant 32-byte header
  [32, 8256)  8224-byte body (XOR-obfuscated float32-LE weights)
  [8256,12288) zero padding

decode(data) -> dict
    "header"   : 32 header bytes (verbatim)
    "raw_body" : 8224 de-obfuscated body bytes (XOR keystream applied)
    "tensors"  : arch.split_body() result — named float32 ndarrays
    "chunks"   : [(tag, declared_len), ...] from arch.parse_chunks()

encode(decoded) -> bytes
    Lossless: re-obfuscates raw_body (XOR is an involution), prepends header,
    zero-pads to SLOT_SIZE.  byte-exact when raw_body came from decode().

roundtrip_ok(data) -> bool
    encode(decode(data)) == data
"""
from __future__ import annotations

import numpy as np

import vxamp as vx
import decode_body as db
import arch


def decode(data: bytes) -> dict:
    """Decode a full 12288-byte vxamp slot into its constituent parts."""
    hdr = vx.header(data)          # bytes [0, 32)
    body_obf = vx.body(data)       # bytes [32, 8256) — still obfuscated

    # De-obfuscate: body XOR keystream.  XOR is an involution so the same
    # operation re-obfuscates in encode().
    raw_body = db.deobfuscate(body_obf)   # plaintext bytes, 8224 B

    # Named float32 tensors (arch delegates deobfuscation internally via db.as_float32)
    tensors = arch.split_body(body_obf)

    # Chunk chain metadata — strip payload ndarrays; keep (tag, declared_len) pairs
    chunks = [(tag, ln) for tag, ln, _ in arch.parse_chunks(body_obf)]

    return {
        "header": hdr,
        "raw_body": raw_body,
        "tensors": tensors,
        "chunks": chunks,
    }


def encode(decoded: dict) -> bytes:
    """Reconstruct a full 12288-byte slot from a decoded dict (byte-exact)."""
    raw_body: bytes = decoded["raw_body"]   # 8224 plaintext bytes
    # Re-obfuscate: applying XOR keystream again reverses the deobfuscation
    ks = db.keystream(len(raw_body))
    body_obf = bytes(np.frombuffer(raw_body, dtype=np.uint8) ^ ks)
    slot = decoded["header"] + body_obf
    # Zero-pad to the full slot size
    return slot + b"\x00" * (vx.SLOT_SIZE - len(slot))


def roundtrip_ok(data: bytes) -> bool:
    """True iff encode(decode(data)) reproduces the original bytes exactly."""
    return encode(decode(data)) == data
