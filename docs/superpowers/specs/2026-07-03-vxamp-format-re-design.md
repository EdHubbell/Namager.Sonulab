# Spec — Reverse-engineer the `vxamp` container format (sub-project 1)

**Date:** 2026-07-03
**Status:** Design, awaiting review
**End goal (parent project):** Load native NAM captures onto the Sonulab StompStation without VoidX,
by reproducing VoidX's `.nam → vxamp` conversion. This spec covers **only sub-project 1: cracking the
`vxamp` container format.** Sub-projects 2 (the converter) and 3 (app upload UI) are out of scope here
and are undesignable until this one answers the repack-vs-refit question.

---

## Background

The pedal stores each amp model in a 30-slot list (`root\amp`, 12288 B/slot, 128-B chunks). VoidX does
**not** upload raw `.nam` files — it converts each `.nam` into a device-specific binary (`vxamp`) first.
That conversion is the last un-reversed piece of the protocol (`PROTOCOL.md` "File upload" section).

On 2026-07-03 we added a read-only `tools/HwCheck --dump-amps` command and pulled all 20 occupied amp
slots to `NAMFiles/VxampDump/*.vxamp`, each name-paired to its source `.nam` in `NAMFiles/FullCaptures/`.

### Confirmed facts (from the 20-model dump; see `PROTOCOL.md`)

- **Fixed output size.** Every model — whether the source `.nam` is a `SlimmableContainer` (2 WaveNet
  submodels; e.g. Pano-Verb: sub0 = 1871 weights, sub1 = 12146 weights) or a plain `WaveNet`
  (13802 weights) — converts to the **same 8256-byte payload**, zero-padded into the 12288-B slot.
  => the device runs **one fixed internal architecture**; VoidX maps any NAM into it.
- **Constant 32-byte header** (bytes 0..31 identical across all 20 blobs):
  - bytes 0–1 = `40 20` = little-endian `0x2040` = **8256** = payload size.
  - bytes 2–7 = `00`; bytes 8–19 = ASCII `"Amp model"` + NULs; bytes 20–23 = tag `yvSD`;
    bytes 24–31 = constant config `21 22 ff 00 9a e7 c4 be`.
- **Per-model data = bytes 32..8255** (8224 bytes).
- **Body is fixed-point quantized, not float.** Decoded as **int8** it lands exactly in
  [−128/127, +1.000], mean ≈ 0 (symmetric int8 signature); int16 also maps to [−1,1]; float32/16/bf16
  all decode to garbage. int8 (8224 values) is the leading hypothesis but int16 (4112 values) is not
  yet ruled out. Source weights do **not** appear verbatim → quantization + tensor reordering (and
  probably per-tensor scale factors) sit on top of any copy.

### The difficulty-deciding open question

Is VoidX a **requantize + repack** of the source weights (→ reproducible offline from the `.nam`,
byte-exact achievable) or a **re-fit / distill** to the fixed device arch (→ needs a training pipeline)?
The rigid fixed-size int8 container points toward repack, but this is unproven and is the first thing
this sub-project must settle.

---

## Goal & definition of done

Chase **byte-exact**; keep **audibly-correct** as the fallback (decision (c) from brainstorm).

**Done when EITHER:**
- **(byte-exact)** a decoder round-trips a real blob into structured tensors+scales, AND a matching
  encoder reproduces at least one captured VoidX `vxamp` **byte-for-byte** from its source `.nam`; OR
- **(fallback)** we have proven VoidX re-fits, documented the container layout well enough to write our
  *own* quantizer into the device architecture, and confirmed via a guarded on-device A/B that a
  self-quantized model loads and sounds like the source.

Plus, in either case: `docs/vxamp-format.md` fully documents the header, tensor order, quantization
scheme, and scale-factor placement; and `PROTOCOL.md` is updated to reference it.

---

## Assets available

- **20 paired examples**: `NAMFiles/FullCaptures/*.nam` ↔ `NAMFiles/VxampDump/*.vxamp` (pair by name).
- **Full NAM JSON** on the input side (open format: architecture, config, flat float32 weight array).
- **Read/write device path already implemented**: `SonuClient.DReadBlobAsync` / `DWriteChunkAsync`,
  `tools/HwCheck` harness (`--dump-amps` read-only; guarded write paths exist for presets).
- **The user can run controlled VoidX + USBPcap captures on demand** (rig confirmed working).
- Fallback source: `C:\Program Files (x86)\VoidX-Control\data\app.so` (Flutter/Dart AOT) contains the
  conversion code + likely the device arch constants.

---

## Approach

**B (top-down) as the frame, A (bottom-up) to fill it in, C (decompile) only if both stall.**

- **B — model-informed:** infer the device's fixed WaveNet architecture from the source NAMs whose size
  matches the 8256-B target (cross-check against `app.so` config strings). The arch yields the exact
  per-tensor weight counts, so the 8224-B body layout largely falls out and both byte-exact and audible
  paths become reachable.
- **A — empirical/diff-driven:** confirm/refine the byte mapping from the static corpus (constant
  regions, entropy, strides, value distributions) and, when a specific ambiguity blocks progress, from
  **controlled captures**.
- **C — decompile `app.so`:** only if A+B cannot resolve the quantization/ordering or the
  repack-vs-refit question empirically.

### Controlled-capture escalation (method power tool)

When static analysis stalls on a specific question, generate synthetic `.nam` files, have the user run
them through VoidX with USBPcap, and diff the outputs. Standard probes:
- all-zero weights; single weight = 1.0 (isolates one weight → output bytes); two files differing in
  exactly one weight (localizes mapping + reveals quantization scale); a minimal-architecture model.
The spec's implementation plan will produce these `.nam` generators and exact capture steps as a
discrete task, invoked only if needed.

---

## Work phases (for the implementation plan)

1. **Corpus prep & re-confirm.** Load all 20 pairs; verify the header/size/body facts above hold across
   every pair; extract the constant vs varying byte map at full resolution.
2. **Encoding determination.** Settle int8 vs int16 vs block-scaled-int; locate scale factor(s)
   (global constant in header? per-tensor? interleaved?). Decode the body to a plausible weight stream.
3. **Architecture inference.** Determine the device's fixed WaveNet arch and its per-tensor weight
   counts; align them to the decoded body to find tensor boundaries and ordering.
4. **Round-trip decoder.** Implement `vxamp → tensors+scales`; validate it reconstructs the exact bytes
   of every dumped blob.
5. **Repack-vs-refit verdict.** Take a source whose arch matches the device target; test whether its
   (re)quantized weights map into the blob body. Record the verdict with evidence.
6. **Encoder (if repack).** Implement `source-weights → vxamp`; validate **byte-exact** against ≥1
   captured pair. (If refit: document the container for a self-quantizer instead, and do the guarded
   on-device A/B for the audible-correctness path.)
7. **Documentation.** Write `docs/vxamp-format.md`; update `PROTOCOL.md` to reference it.

Controlled-capture generation is a conditional task, pulled in only where phases 2–3 or 5 require it.

## Tooling & layout

- RE spike in **Python** (numpy) under `tools/vxamp-re/` — fast iteration on quantization/layout, plus
  the `.nam` synthetic generators and capture-analysis scripts. This stays as reference/validation.
- The **production converter** (sub-projects 2–3) will be ported to **C# in `src/Sonulab.Core`**,
  matching the repo; not built here beyond, at most, a decoder stub if convenient.
- All device interaction remains **read-only** for this sub-project except the single optional guarded
  on-device A/B in phase 6 (empty/throwaway amp slot, backed up first, per repo write-safety rules).

## Risks & mitigations

- **VoidX re-fits (not a repack).** → byte-exact becomes impossible; fall back to the documented
  self-quantizer + audible A/B. Detected explicitly in phase 5, so it fails fast, not late.
- **Per-tensor/block scales hidden in the body.** → phase 2 isolates scales via single-weight controlled
  captures before attempting full layout.
- **Device arch mis-inferred.** → cross-check inferred tensor sizes against `app.so` strings (C) before
  committing to a layout.
- **int16 not int8.** → phase 2 resolves via controlled single-weight captures (a known ±1.0 weight
  reveals element width and scale directly).

## Explicitly out of scope

- Sub-project 2: the `.nam → vxamp` converter as a shipping C# feature.
- Sub-project 3: app UI for amp-model upload/management.
- IR (`wav_44100`) conversion — separate format, separate effort.
- Any non-guarded device writes.
