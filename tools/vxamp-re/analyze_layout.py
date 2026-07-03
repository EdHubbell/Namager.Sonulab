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
