"""NAM runner: load a .nam file and run audio through it (numpy WaveNet forward).

Supports the two architectures in the corpus:

- **WaveNet** (standard neural-amp-modeler export, e.g. Princeton Clean 3 SM57):
  ``config.layers`` is a list of layer arrays, each with ``input_size``,
  ``condition_size``, ``head_size``, ``channels``, scalar ``kernel_size``,
  ``dilations``, ``activation`` (string), ``gated``, ``head_bias``.
  Per array: rechannel (1x1, no bias) -> N dilated causal convs, each
  ``z = act(conv(x) + input_mixin(cond))`` (gated: ``act(a)*sigmoid(b)``),
  skip accumulation ``head += z``, residual ``x = x + 1x1(z)``; then the
  accumulated skips pass through ``head_rechannel`` (1x1) whose output seeds
  the next array's skip accumulator. Final output = ``head_scale * head``.
  ``weights`` is flat, consumed in declaration order; the LAST float is
  ``head_scale`` (as in NeuralAmpModelerCore).

- **SlimmableContainer** (VoidX-fork, e.g. Pano-Verb): ``config.submodels``
  is a list of ``{max_value, model}``; the full submodel (``max_value == 1.0``)
  is a fork WaveNet whose layer group carries per-conv ``kernel_sizes`` /
  ``dilations``, a per-conv ``activation`` list (dicts), and a ``head`` conv
  (kernel 16). Same rechannel/mixin/1x1/skip structure; head conv is applied
  to the skip sum, then x head_scale (again the last flat weight).
  Weight-count identity validated against the corpus: C=3 -> 1871,
  C=8 -> 12146, standard -> 13802 (exact, incl. trailing head_scale).

All exotic fork features (FiLM, gating modes, bottleneck != channels,
head1x1, grouped convs, secondary activations) are inactive across the whole
corpus; this runner raises rather than silently mis-rendering if one appears.

Causality: convs are zero-left-padded ((K-1)*dilation), so output[n] depends
only on input[<=n] and len(out) == len(in) -- equivalent to the streaming
plugin with silent history.

Prewarm + DC removal (mirrors the official runtime): layer biases give the
net a cold-start transient (internal feature maps are non-zero constants,
zero-initialized histories are not) and a small constant DC offset on
silence.  ``nam::DSP::prewarm()`` runs a receptive field of silence for the
former; the NeuralAmpModeler plugin runs a DC blocker after the model for
the latter.  ``process()`` does both: the input is left-padded with a full
receptive field of silence (output trimmed back), and the model's settled
silence level is subtracted, so silence in -> exactly 0 out.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np

try:
    import vxamp as vx
except ModuleNotFoundError:  # standalone use without the pytest conftest shim
    import sys

    sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "vxamp-re"))
    import vxamp as vx


# ---------------------------------------------------------------------------
# flat-weight consumption

class _WeightReader:
    def __init__(self, flat):
        self._flat = np.asarray(flat, dtype=np.float32)
        self._pos = 0

    def take(self, *shape: int) -> np.ndarray:
        n = int(np.prod(shape))
        chunk = self._flat[self._pos : self._pos + n]
        if chunk.size != n:
            raise ValueError(
                f"weight underrun: wanted {n} at {self._pos}, have {self._flat.size}"
            )
        self._pos += n
        return chunk.reshape(shape)

    def remaining(self) -> int:
        return self._flat.size - self._pos


# ---------------------------------------------------------------------------
# activations

def _make_activation(spec):
    """spec: string name (standard NAM) or {'type': ..., ...} dict (fork)."""
    if isinstance(spec, dict):
        kind = spec["type"]
        if kind == "LeakyReLU":
            slope = np.float32(spec.get("negative_slope", 0.01))
            return lambda z: np.where(z >= 0.0, z, slope * z)
        spec = kind  # fall through for parameter-free types
    if spec == "Tanh":
        return np.tanh
    if spec == "ReLU":
        return lambda z: np.maximum(z, np.float32(0.0))
    if spec == "Sigmoid":
        return _sigmoid
    if spec == "Hardtanh":
        return lambda z: np.clip(z, -1.0, 1.0)
    raise ValueError(f"unsupported activation: {spec!r}")


def _sigmoid(z: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-z))


# ---------------------------------------------------------------------------
# causal dilated conv (PyTorch cross-correlation convention, zero left-pad)

def _causal_conv(x: np.ndarray, w: np.ndarray, b, dilation: int) -> np.ndarray:
    """x: (Cin, N); w: (Cout, Cin, K); returns (Cout, N). out[n] uses x[<=n]."""
    cout, cin, k = w.shape
    n = x.shape[1]
    pad = (k - 1) * dilation
    xp = np.pad(x, ((0, 0), (pad, 0))) if pad else x
    y = np.zeros((cout, n), dtype=np.float32)
    for tap in range(k):  # tap k-1 aligns with the current sample
        y += w[:, :, tap] @ xp[:, tap * dilation : tap * dilation + n]
    if b is not None:
        y += b[:, None]
    return y


# ---------------------------------------------------------------------------
# unified parsed structure (both variants map onto this)

@dataclass
class _Layer:
    conv_w: np.ndarray          # (mid, C, K)
    conv_b: np.ndarray          # (mid,)
    mixin_w: np.ndarray         # (mid, cond, 1)
    one_w: np.ndarray | None    # (C, C, 1) residual 1x1
    one_b: np.ndarray | None    # (C,)
    dilation: int
    activation: object          # callable
    gated: bool
    channels: int


@dataclass
class _LayerArray:
    rechannel_w: np.ndarray     # (C, in, 1), no bias
    layers: list
    head_w: np.ndarray          # (head_out, C, Kh)
    head_b: np.ndarray | None   # (head_out,)


@dataclass
class NamModel:
    arch: str
    sample_rate: int | None
    _arrays: list
    _head_scale: float

    @property
    def receptive_field(self) -> int:
        """Total look-back of the net in samples: sum of (K-1)*dilation."""
        rf = 0
        for arr in self._arrays:
            for lyr in arr.layers:
                rf += (lyr.conv_w.shape[2] - 1) * lyr.dilation
            rf += arr.head_w.shape[2] - 1
        return rf

    @property
    def _silence_level(self) -> float:
        """Settled model output on silence (the model's DC offset)."""
        if not hasattr(self, "_dc"):
            self._dc = float(self._raw(np.zeros(self.receptive_field + 8, np.float32))[-1])
        return self._dc

    def process(self, x: np.ndarray) -> np.ndarray:
        """Run audio through the model. float32 in/out, same length, causal.

        Prewarmed (a receptive field of silence precedes the buffer) and
        DC-removed (the model's silence level is subtracted), so the output
        is the settled audio response and silence maps to exactly zero.
        """
        x = np.asarray(x, dtype=np.float32).reshape(-1)
        n = x.size
        pad = self.receptive_field
        y = self._raw(np.pad(x, (pad, 0)))[pad : pad + n]
        return (y - np.float32(self._silence_level)).astype(np.float32)

    def _raw(self, x: np.ndarray) -> np.ndarray:
        """Cold-start WaveNet forward (zero-initialized conv histories)."""
        cond = x[None, :]                     # condition = raw input (1, N)
        h = cond                              # layer-stack input
        skips = None                          # accumulated head input
        for arr in self._arrays:
            h = arr.rechannel_w[:, :, 0] @ h
            for lyr in arr.layers:
                z = _causal_conv(h, lyr.conv_w, lyr.conv_b, lyr.dilation)
                z += lyr.mixin_w[:, :, 0] @ cond
                if lyr.gated:
                    c = lyr.channels
                    z = lyr.activation(z[:c]) * _sigmoid(z[c:])
                else:
                    z = lyr.activation(z)
                skips = z if skips is None else skips + z
                h = h + (lyr.one_w[:, :, 0] @ z + lyr.one_b[:, None])
            skips = _causal_conv(skips, arr.head_w, arr.head_b, 1)
        return np.float32(self._head_scale) * skips[0]


# ---------------------------------------------------------------------------
# parsers: .nam config + flat weights -> _LayerArray list

def _require(cond: bool, what: str) -> None:
    if not cond:
        raise ValueError(f"unsupported .nam feature: {what}")


def _parse_standard_wavenet(config: dict, flat) -> tuple[list, float]:
    _require(config.get("head") is None, "top-level WaveNet head module")
    r = _WeightReader(flat)
    arrays = []
    for lg in config["layers"]:
        cin = lg["input_size"]
        cond = lg["condition_size"]
        ch = lg["channels"]
        k = lg["kernel_size"]
        gated = lg["gated"]
        mid = 2 * ch if gated else ch
        act = lg["activation"]
        rechannel = r.take(ch, cin, 1)
        layers = []
        for d in lg["dilations"]:
            layers.append(_Layer(
                conv_w=r.take(mid, ch, k), conv_b=r.take(mid),
                mixin_w=r.take(mid, cond, 1),
                one_w=r.take(ch, ch, 1), one_b=r.take(ch),
                dilation=int(d), activation=_make_activation(act),
                gated=gated, channels=ch,
            ))
        head_w = r.take(lg["head_size"], ch, 1)
        head_b = r.take(lg["head_size"]) if lg["head_bias"] else None
        arrays.append(_LayerArray(rechannel, layers, head_w, head_b))
    head_scale = float(r.take(1)[0])
    _require(r.remaining() == 0, f"{r.remaining()} unconsumed weights")
    return arrays, head_scale


def _parse_fork_wavenet(config: dict, flat) -> tuple[list, float]:
    """VoidX-fork WaveNet (inside SlimmableContainer submodels)."""
    _require(config.get("head") is None, "top-level fork head module")
    r = _WeightReader(flat)
    arrays = []
    for lg in config["layers"]:
        cin = lg["input_size"]
        cond = lg["condition_size"]
        ch = lg["channels"]
        _require(lg.get("bottleneck", ch) == ch, f"bottleneck {lg.get('bottleneck')} != channels")
        _require(lg.get("groups_input", 1) == 1 and lg.get("groups_input_mixin", 1) == 1,
                 "grouped convolutions")
        _require(not lg.get("head1x1", {}).get("active", False), "active head1x1")
        l1 = lg.get("layer1x1", {"active": True, "groups": 1})
        _require(l1.get("active", True) and l1.get("groups", 1) == 1, "layer1x1 variant")
        for f in ("conv_pre_film", "conv_post_film", "input_mixin_pre_film",
                  "input_mixin_post_film", "activation_pre_film", "activation_post_film",
                  "layer1x1_post_film", "head1x1_post_film"):
            _require(not lg.get(f, {}).get("active", False), f"active {f}")
        n_convs = len(lg["kernel_sizes"])
        gating = lg.get("gating_mode", ["none"] * n_convs)
        secondary = lg.get("secondary_activation", [None] * n_convs)
        _require(all(g == "none" for g in gating), f"gating_mode {gating}")
        _require(all(s is None for s in (secondary or [])), "secondary_activation")

        rechannel = r.take(ch, cin, 1)
        layers = []
        for k, d, act in zip(lg["kernel_sizes"], lg["dilations"], lg["activation"]):
            layers.append(_Layer(
                conv_w=r.take(ch, ch, k), conv_b=r.take(ch),
                mixin_w=r.take(ch, cond, 1),
                one_w=r.take(ch, ch, 1), one_b=r.take(ch),
                dilation=int(d), activation=_make_activation(act),
                gated=False, channels=ch,
            ))
        hd = lg["head"]
        head_w = r.take(hd["out_channels"], ch, hd["kernel_size"])
        head_b = r.take(hd["out_channels"]) if hd.get("bias", False) else None
        arrays.append(_LayerArray(rechannel, layers, head_w, head_b))
    head_scale = float(r.take(1)[0])
    _require(r.remaining() == 0, f"{r.remaining()} unconsumed weights")
    return arrays, head_scale


def _full_submodel(nam: dict) -> tuple[int, dict]:
    subs = nam["config"]["submodels"]
    idx = max(range(len(subs)), key=lambda i: subs[i]["max_value"])
    _require(float(subs[idx]["max_value"]) == 1.0,
             f"no full submodel (max max_value = {subs[idx]['max_value']})")
    return idx, subs[idx]["model"]


def load_nam_model(path) -> NamModel:
    nam = vx.load_nam(path)
    arch = nam["architecture"]
    sr = nam.get("sample_rate")
    weights = dict(vx.nam_weights(nam))
    if arch == "WaveNet":
        arrays, head_scale = _parse_standard_wavenet(nam["config"], weights["root"])
    elif arch == "SlimmableContainer":
        idx, model = _full_submodel(nam)
        _require(model["architecture"] == "WaveNet",
                 f"submodel architecture {model['architecture']}")
        sr = sr if sr is not None else model.get("sample_rate")
        arrays, head_scale = _parse_fork_wavenet(model["config"], weights[f"sub{idx}"])
    else:
        raise ValueError(f"unhandled architecture: {arch}")
    return NamModel(
        arch=arch,
        sample_rate=int(sr) if sr is not None else None,
        _arrays=arrays,
        _head_scale=head_scale,
    )
