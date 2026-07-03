# Spec — NAM → vxamp Distiller (sub-project 2)

**Date:** 2026-07-03
**Status:** Design, awaiting review
**Parent goal:** Get native NAM captures onto the Sonulab StompStation without VoidX.
**This sub-project:** Build the **distiller** — convert a `.nam` into a device `.vxamp` by fitting the
device's Wiener–Hammerstein FIR-cascade to the NAM's response, calibrated to land close to VoidX's own
output. The **Amps-tab upload UI is a separate follow-on (sub-project 2b)** and is out of scope here,
except for a minimal guarded upload used to ear-validate the distiller.

---

## Background (established in sub-project 1)

The device amp format is fully reverse-engineered (`docs/vxamp-format.md`, `tools/vxamp-re/`):
- A `.vxamp` is a 12288-byte slot: 32-byte constant header + 8256-byte payload; the per-model body is
  8224 bytes = **float32-LE weights XOR-obfuscated** by a recovered keystream.
- The de-obfuscated body is a **Wiener–Hammerstein FIR-cascade in a TLV container** (`arch.py`):
  `pre_fir` (1024 float32 taps) ‖ `"G2"` chunk (3-float header + `g2_ir` 1024-tap cabinet IR) ‖
  `"nlmix"` chunk (4-float header + 1 mix scalar). 2056 float32 total.
- **Verdict = refit:** VoidX distills the source NAM (a neural WaveNet) into this cheaper fixed model.
  Byte-exact reproduction from a `.nam` is impossible; the path is to *fit our own* model into the
  container — this sub-project.

**Reusable assets:** `decode_body` (`deobfuscate`, `as_float32`, `weights`, `keystream`,
`KEYSTREAM_BASE`), `arch` (`DEVICE_ARCH`, `tensor_sizes`, `split_body`, `parse_chunks`), `codec`
(`decode`/`encode`/`roundtrip_ok`), `nam_to_vxamp.write_vxamp` (byte-exact container writer),
`vxamp` (`load_nam`, `nam_weights`, `pairs`), and `HwCheck --dump-amps`. Plus **14 paired
`(.nam, VoidX-vxamp)` examples** in `NAMFiles/`.

**Key device facts that shape this work:**
- The nonlinearity is tiny: 4 header floats + 1 mix scalar; corpus scalar range 0..0.67; **0 (fully
  linear) on 7 of 20 amps**. The device is a **clean / edge-of-breakup** machine.
- The device nonlinearity's exact *function* is still **PROVISIONAL** (we know its ~5 parameters, not
  the shape they drive). Pinning it is phase 1 here.

---

## Goal & definition of done (v1)

Convert a clean/edge-of-breakup `.nam` into a `.vxamp` whose *device-simulated* response matches the
NAM's response, at fidelity **at least as good as VoidX's own** on the same NAM.

**Done when ALL of:**
1. **Nonlinearity pinned** — the firmware's nonlinearity form is identified well enough to fit into
   (documented, no longer provisional); or, if it proves negligible for clean amps, that is shown and
   v1 fits a near-pure linear cascade.
2. **Offline fidelity** — on the clean/edge-of-breakup subset of the dataset, the distiller's output
   `.vxamp`, run through the device simulator, matches the NAM's response within a stated error metric —
   **log-magnitude spectral error + time-domain NRMSE across the probe signals** (small-signal sweep
   for the linear match, level-stepped sines for the nonlinear match) — and its error is **≤ VoidX's
   error** (VoidX's own vxamp run through the same simulator is the yardstick) on a majority of that
   subset. The exact thresholds are set in the plan after the device-simulator + prober land and we can
   read VoidX's actual error numbers.
3. **VoidX closeness** — reported per pair: how close our fitted `pre_fir`/`g2_ir`/`nlmix` parameters
   are to VoidX's (a calibration/quality signal, not a hard gate).
4. **On-pedal confirmation** — ≥1 distilled clean amp uploaded via the minimal guarded upload and
   confirmed to sound right by ear (the one hardware-dependent step; needs the user + pedal).

**v1 fidelity scope:** clean → edge-of-breakup. High-gain is a documented stretch goal, NOT a v1
requirement (the device's constrained nonlinearity may cap it regardless of fitting).

---

## Architecture — components (each independently testable)

1. **NAM runner** (`nam_runner.py`) — load a `.nam`, process a float audio buffer through it. Extends
   the numpy WaveNet forward stood up in sub-project 1; handles `SlimmableContainer` (pick the
   appropriate submodel) and plain `WaveNet`. Validated against known outputs.
2. **Device-model simulator** (`device_sim.py`) — numpy sim of the device forward model:
   `y = g2_ir ⊛ nl( pre_fir ⊛ x )`, parameterized exactly as `arch.split_body` returns. Consumes a
   decoded vxamp (or raw tensors) and processes audio. Used to (a) measure VoidX's own fidelity
   (VoidX vxamp-sim vs NAM = quality ceiling) and (b) validate our fitted output before upload.
3. **Response prober** (`probe.py`) — generate calibrated test signals at the device sample rate
   (confirm rate; IR list is `wav_44100`, so likely 44.1 kHz) and capture NAM output: small-signal
   sweeps/impulses → linear response; level sweeps (stepped-amplitude sines) → static nonlinearity
   curve. Wiener–Hammerstein identification inputs.
4. **WH fitter** (`fit.py`) — fit the probed response into the device parameterization: `pre_fir`
   (1024 taps) + the constrained nonlinearity (4 header floats + mix scalar) + `g2_ir` (1024 taps).
   The pre-vs-post linear split (classic WH ambiguity) and level/loudness normalization are calibrated
   to VoidX's conventions using the dataset.
5. **Packer** — reuse `nam_to_vxamp.write_vxamp` + obfuscation to emit the 12288-byte `.vxamp`.

Plus a **minimal guarded upload** (`HwCheck --upload-amp <vxamp> <slot>`, the deferred Task 9 from
sub-project 1): backs up the target amp slot, `dwrite`s name+payload+terminator, reads back and verifies.
For ear-validation only; the full Amps tab is sub-project 2b.

---

## Phase 1 (highest risk, first) — pin the nonlinearity

The distiller cannot fit until the firmware's nonlinearity form is known. Identify it empirically using
the dataset, especially **controlled probes**:
- A **linear NAM** (a clean capture with confirmed `nlmix`=0, or a synthesized near-linear model) →
  isolates the linear FIR path and confirms `pre_fir`/`g2_ir` fitting in the absence of nonlinearity.
- **Progressively driven NAMs** → observe how the `nlmix` scalar + 4 header floats track drive amount,
  revealing the nonlinearity's shape and parameterization.
Deliverable: a documented model of `nl(·)` (the function the ~5 nlmix parameters drive), or a finding
that it is negligible for the v1 clean scope. This phase also calibrates the pre/cab linear split.

If Phase 1 shows the nonlinearity cannot be pinned from the static dataset, escalate to targeted
controlled captures (user runs specific synthetic NAMs through VoidX) — same technique as sub-project 1.

---

## Data flow & dataset

- **Distill:** `.nam` → NAM runner → probe → WH fitter → packer → `.vxamp`.
- **Calibrate/validate:** dataset of `(.nam, VoidX-vxamp)` pairs (14 existing + more) drives fitter
  conventions and grades output — per pair: (i) our response vs the NAM's response; (ii) our vxamp
  params vs VoidX's.
- **Dataset generation** (user effort): diverse downloaded real NAMs for coverage + a few controlled
  synthetic NAMs for Phase 1. Order of a few dozen pairs. Generated by uploading each `.nam` in VoidX,
  then `HwCheck --dump-amps` to capture the `.vxamp`, paired by name (existing `vxamp.pairs()` convention).

---

## Deliverable & seam to the app

- Python (numpy/scipy) under `tools/distiller/`, reusing sub-project-1 modules.
- Produces a `.vxamp` file — the stable seam to **sub-project 2b (the Amps tab)**, which will consume
  exactly that artifact. The app's upload feature is designed against this file, independent of distiller
  internals.
- **Integration note (2b, not solved here):** the app is C#; it will invoke the Python distiller as a
  bundled CLI/subprocess, or the settled fitter gets ported to C#. Flagged for 2b.

---

## Testing

Offline, against the corpus and dataset, TDD + per-task review (as sub-project 1):
- NAM runner: matches known NAM outputs on reference buffers.
- Device simulator: round-trips a decoded corpus vxamp (sim output stable/finite; linear-only amps
  reproduce their `pre_fir ⊛ g2_ir` response).
- WH fitter: recovers a known synthetic amp (fit a device-sim-generated target back to its own tensors
  within tolerance).
- Dataset-level: fidelity error thresholds vs NAM; closeness vs VoidX; assert our error ≤ VoidX error
  on the clean subset majority.

---

## Risks & mitigations

- **Nonlinearity unpinnable statically** → Phase 1 escalates to controlled VoidX captures (user-run).
- **Device sample rate / resampling wrong** → confirm the device rate early (probe against a
  linear-amp pair: our sim of VoidX's vxamp must match that NAM's response only if the rate is right).
- **WH pre/post split ambiguity** → calibrate to VoidX using pairs; the split only needs to be
  *consistent + sounds right*, not unique.
- **High-gain can't be reproduced** → explicitly out of v1 scope; documented as a device ceiling.
- **NAM runner can't load some arch variants** → v1 supports the corpus arch set
  (`SlimmableContainer`, `WaveNet`); other variants deferred.

## Explicitly out of scope

- Sub-project 2b: the Amps-tab UI (list/upload/backup/manage amps in `Sonulab.App`).
- High-gain fidelity.
- Porting the distiller to C# / final app integration (2b decision).
- IR (`root\ir`) handling.
