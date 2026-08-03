# Spec — Preset Level: a visible control + volume matching

**Date:** 2026-08-03
**Status:** Design approved (Ed, 2026-08-03). Ready for writing-plans.

## Goal

Switching presets on the pedal can produce a large jump in output volume. Sometimes that is
intentional (a lead boost); often it is just an artifact of different amp models having been
distilled at different loudnesses. Today there is no way to see how loud a preset is relative to the
others, and no way to say "match this one to preset #2."

The pedal already has the right control and NAMager does not show it at all. This spec makes it
visible as the top section of the parameter editor, and adds a one-click action that computes the
trim needed to match another preset's loudness.

## Scope & non-goals

- **In scope:** a `Level` block pinned above `Gate` in the parameter editor, holding the pedal's
  per-preset output trim as a slider with explanatory text; a "match to another preset" action; an
  offline loudness estimator built on the existing distiller DSP; a per-device cache of amp-model
  loudness; offline unit tests; a hardware-validation checklist.
- **Non-goals:** bulk "normalize the whole bank" (deferred — see *Deferred* below); surfacing the
  per-preset BPM node; reading the device VU meters; any use of the USB audio path; calibrating the
  `amp\vol` taper against hardware.

## Background verified against the code and device (2026-08-03)

- **The control exists in firmware.** `docs/probe-output.txt:203`:
  `root\app\output\pst\level` — desc `"Preset Level"`, `type` float, **min −20 / max +20 dB,
  def 0**, `unit` dB, `dec` 1. It sits under `root\app\output` (desc "Master") →
  `pst` (desc "Preset Specific"), i.e. it is a post-everything per-preset gain trim, and it is saved
  into the `.pst` (line 115 of `presets/Pano-Verb.pst`).
- **It is invisible because the editor never looks there.**
  `ParameterEditorViewModel.Blocks_InScope` (`src/Namager.App/ViewModels/ParameterEditorViewModel.cs:16`)
  is `{ gate, exp, comp, amp, eq, ir, delay, reverb }`. `LoadCoreAsync` calls `browse root\app`,
  which *does* return this node, and then discards it because no in-scope prefix matches.
- **It is unused.** `root\app\output\pst\level` reads `0.000000` in all 13 files in `presets/`.
  Full ±20 dB of headroom; nothing to preserve.
- **The only sibling leaf is a BPM node** — `root\app\output\pst\tmp` ("Preset TEMPO", 30–240 BPM,
  `probe-output.txt:204`). Deliberately not surfaced.
- **Floats already render as sliders.** `ParameterEditorView.axaml:56` binds a `Slider` to
  `Min`/`Max`/`Number`, with the reset button and `IsChangedFromDefault` highlight supplied by
  `ParameterFieldViewModel`. A new float field needs no new template.
- **Block headers already carry conditional icons.** `BlockSectionViewModel.ShowEqIcon` drives a
  `PathIcon` at `ParameterEditorView.axaml:38`, alongside the `Icon.Power` one at line 33.
- **The distiller already measures amp-model loudness.** `Distiller.LoudnessNormalize`
  (`src/Sonulab.Distill/Distiller.cs:23`) simulates `WhTensors` on the fixed `DriveSignal.Get()`
  (`src/Sonulab.Distill/FirFitter.cs:9`) through `DeviceSim.Simulate` and measures `Dsp.RmsDb`.
  `VxampCodec.Decode` turns an on-device slot blob back into those tensors.
- **`Sonulab.Distill` does not reference `Sonulab.Core`** (checked in both `.csproj` files). The new
  DSP must not introduce that edge.

## Why an offline estimate is credible here

A survey of `presets/*.pst` shows the gain-staging parameters take only two configurations across
the entire bank:

```
                         amp\vol    amp\gain   eq\level   pst\level   ir   comp
Bassman 5F6A             50.000     0.000      0.000      0.000       OFF  OFF
Dumble Steel SS          56.831     0.984      5.880      0.000       OFF  OFF
… (13 presets, those two rows only)
```

So virtually all of the preset-to-preset volume spread comes from **the amp model itself** — the
quantity the distiller DSP computes exactly, and which `tools/distiller/distill.py:113` records as
having **std ~3.3 dB** across the paired corpus. The parameters that would have to be *guessed* at
sit at or near their firmware defaults almost everywhere.

Two consequences shape the design: the estimator is worth building because its dominant term is
exact, and it must be honest about the rest.

## Design decisions

1. **K-weighted loudness (ITU-R BS.1770), not the distiller's broadband RMS.** The goal is matching
   *perceived* level between a dark Bassman and a bright JC120; broadband RMS misjudges that by a
   couple of dB. `DeviceReferenceDb` and `LoudnessNormalize` keep using RMS — the distiller's
   parity with the Python oracle must not move.
2. **Only differences are claimed.** Nothing in the UI presents an absolute "this preset is X dBFS
   at the output jack"; the estimator's output is meaningful only as a difference between presets.
3. **Unmodelable parameters are flagged, never guessed at.** Anything not derivable from first
   principles raises a visible "check by ear" note rather than producing a confident wrong number.
4. **"Non-default" is judged against the device's own `def` values** from `browse root\app`
   (already loaded as `NodeSchema`) — no baked parameter table, consistent with the
   blocklist-not-allowlist convention in CLAUDE.md.
5. **The Level block is built from an explicit node path, not a prefix sweep.** `root\app\output` is
   the *global* Master block; sweeping it would pull in the global volume, the global tempo, and the
   MIDI controller sub-tree. An explicit path excludes the BPM node by construction and needs no
   `hidden-params.json` entry.
6. **Matching sets the slider dirty; it does not write.** The user reviews the proposed number and
   presses the editor's existing **Save**, exactly like every other parameter in the panel.

## Part 1 — the Level block

**`BlockSectionViewModel`** gains `ShowLevelIcon`, alongside the existing `ShowEqIcon`. The header
already renders conditional `PathIcon`s by exactly this idiom; an icon-key indirection would be a
bigger change for no gain at two icons.

**`Icons.axaml`** gains two Material Design Icons geometries, matching the existing
`PathIcon`-only convention (**no third-party icon library** — CLAUDE.md):

- `Icon.VolumeHigh` — MDI `volume-high`, the block header icon.
- `Icon.VolumeEqual` — MDI `volume-equal`, the match button.

Both path strings are to be copied from the MDI set during implementation and checked visually at
14×14 and 16×16 before committing — not transcribed from memory.

**`ParameterEditorViewModel.LoadCoreAsync`** builds one synthetic section ahead of the
`Blocks_InScope` loop:

```csharp
/// <summary>The pedal's per-preset output trim, shown as its own top-of-editor block.
/// Not part of Blocks_InScope: `root\app\output` is the GLOBAL Master block, and the only other
/// leaf under `pst` is a per-preset BPM we deliberately don't surface. Addressed by explicit path
/// so nothing else under `output` can leak in.</summary>
public const string PresetLevelPath = @"root\app\output\pst\level";
```

- Header label `"Level"`, via `LabelService` so it stays localizable.
- One `ParameterFieldViewModel` over the browsed record — the existing float template renders the
  slider, reset button and changed-from-default highlight for free.
- **No extra device round-trip:** the record is already in the `browse root\app` response.
- Inserted at `Blocks[0]`, **expanded by default** (every other block defaults collapsed) because it
  is the headline control. Once the user collapses it, the existing per-session `_expansion` memory
  takes over.
- Dirty tracking, `IsDirty`, and `SaveAsync` come from the existing field plumbing untouched.
- When the browse response has no such node (older firmware), the block is simply not added — the
  editor must not fail to load.

**`ParameterEditorView.axaml`** gets, inside that block:

- The header `PathIcon` bound to `ShowLevelIcon`, mirroring the Equalizer icon at line 38.
- An explanatory line below the slider, using the existing `TextBlock.section-label` /
  `TextBlock.slot-message` styles (theme tokens only — never hex literals in a view):
  > *Trims this preset's output after every effect — use it to match loudness between presets. It
  > doesn't change the tone.*
- The match button beside the explanation: `Icon.VolumeEqual`, `ToolTip.Tip` = *"Match this
  preset's volume to another preset"*.

## Part 2 — matching to another preset

`MatchVolumeCommand` on `ParameterEditorViewModel`:

1. Opens `MatchPresetDialog` — a ComboBox of the non-empty preset slots excluding the loaded one,
   modelled on `src/Namager.App/Views/SlotPickerDialog.axaml` (same shape, same `accent-outline`
   confirm button).
2. Under `_status.BeginOperation("Matching volume…")`: estimates **this** preset from the fields
   already in `Blocks` plus its amp blob, then reads the target preset
   (`DeviceRepository.ReadPresetAsync`) and its amp blob.
3. Sets the Level field to `clamp(targetLufs + targetTrim − thisLufs, −20, +20)`, leaving it
   **dirty and unwritten**.
4. Reports on the status bar: the applied delta, any "check by ear" flags from either preset, and
   whether the value saturated at ±20.

The `amp\vol` taper flag is handled here rather than in the model. `LevelModel` raises it per-preset
whenever `vol` ≠ `def`, which is a statement of fact; but the assumed taper **cancels out of the
difference** when both presets share a `vol` value. `MatchVolumeCommand` therefore surfaces that
particular flag only when the two presets' `amp\vol` values actually differ. Every other flag is
surfaced whenever either preset raises it.

Every failure path (target read fails, amp blob unreadable, link death) surfaces through
`ErrorMessage` / `_status.Failure` and leaves the slider untouched — the same contract
`LoadOneAsync` and `SaveAsync` already follow. A `[RelayCommand] async` escape here would be an
unhandled UI-thread rethrow, so nothing may propagate.

## Part 3 — the estimator

### `src/Sonulab.Distill/Loudness.cs` (new)

ITU-R BS.1770 K-weighting (a high-shelf and a high-pass biquad, coefficients derived for
`DeviceSim.SampleRate` = 44100) followed by mean-square → LUFS. Pure; depends only on `Dsp`.

Gating: the **absolute gate only** (−70 LUFS, over 400 ms blocks with 75 % overlap). The relative
gate is deliberately omitted — the input is a single fixed, continuously-excited drive signal, not
program material with silence to exclude, so the relative gate would add a discontinuity in the
measure for no benefit. Documented in the file so the omission reads as a decision, not an
oversight.

### `src/Sonulab.Distill/LevelModel.cs` (new)

```csharp
public sealed record PresetLevelEstimate(
    double RelativeLufs,        // K-weighted level of the chain, EXCLUDING pst\level
    double CurrentTrimDb,       // the preset's existing root\app\output\pst\level
    IReadOnlyList<string> Unmodeled);

public static PresetLevelEstimate Estimate(
    IReadOnlyDictionary<string, string> presetValues,      // node path -> raw JSON value
    ReadOnlySpan<byte> vxampSlot,
    byte[]? ir1, byte[]? ir2,
    IReadOnlyDictionary<string, double> schemaDefaults);
```

Takes a plain path→value dictionary rather than a `PresetDocument`, so `Sonulab.Distill` keeps no
dependency on `Sonulab.Core`; `Namager.App` does the glue from either a `PresetDocument` or the
editor's loaded fields.

Simulates `DriveSignal.Get()` through:

| Stage | Treatment |
|---|---|
| `amp\on_off` OFF | amp bypassed; flag |
| `amp\gain` (dB) | exact input scaling |
| amp model | `VxampCodec.Decode` → `DeviceSim.Simulate` — **exact** |
| `amp\vol` (%) | `20·log10(pct/50)` — **the one modeling assumption**, isolated in a single named function so a later meter calibration can replace it. Flagged whenever `vol` ≠ `def` |
| `eq\level` (dB) | exact |
| `eq\low/mid/treble` | not modeled; flag when ≠ `def` |
| `ir\on_off` ON | convolve `IrFormat.Decode(blob)`; `lo_cut`/`hi_cut` not modeled, flag when ≠ `def`. Same for `ir2` |
| `comp`, `mod`, `delay`, `reverb`, `gate` ON | not modeled; flag |
| `amp\sag/depth/presence` | not modeled; flag when ≠ `def` |
| `output\pst\level` | **excluded** from the estimate and returned separately — the proposal is arithmetic on top of it |

### Caching (corrected 2026-08-03, during writing-plans)

An amp blob is 96 chunks (~3 s), so it is worth not re-reading. This spec originally called for an
`AmpLoudnessCache` — a persistent per-device store of each amp slot's loudness, mirroring
`PresetUsageCache`. **That does not work and is not being built.** `LevelModel.Estimate` needs the
amp *blob*: the model is nonlinear (`nlmix` sits mid-chain between the two FIRs), so the amp's
contribution is not a scalar offset that can be added after the fact, and a cached loudness cannot
short-circuit the read it was meant to avoid.

What is built instead: amp and IR blobs are **memoized per view-model instance**, keyed by
`(list path, slot name)`. Both sides of a comparison usually name the same amp, so a match costs
one 96-chunk read rather than two, and a second match in the same session costs none.

If the deferred bulk-normalize feature later wants persistence, the correct shape is a
**preset-keyed estimate cache** — key `(deviceId, slot, presetName, hash of the level-relevant
parameter values)`, value `RelativeLufs` — which short-circuits the whole chain rather than one
term of it.

## Deferred — normalize the whole bank

A "Normalize all presets…" review dialog off the Presets tab (estimate every preset, propose trims
against a chosen reference, review/override, apply) is deliberately out of this cycle. The
per-preset control above is what makes the parameter visible and usable, and is the direct answer to
"match this one to preset #2."

When it is built, the bulk apply should be **byte-exact** —
`SlotBlobService.ReadAndArchiveAsync` → `PresetDocument.SetValueJson` → `UploadAsync`, ~10 s/slot
per the PROTOCOL.md VERDICT (2026-07-04) — rather than select+save. One line changes per preset and
the rest of the blob stays byte-identical, which matters at a 30-slot blast radius in a way it does
not for a single preset the user is already editing.

## Testing

- **`Sonulab.Distill.Tests`** — K-weighting sanity: a 1 kHz sine reads ≈ 0 LU offset, and a 100 Hz
  sine of equal RMS reads lower; the absolute gate drops a −80 dBFS block and keeps a −60 dBFS one;
  scaling a signal by *g* moves the measure by exactly `20·log10(g)`. `LevelModel` invariants: two presets differing
  only by `eq\level` +6 dB estimate exactly 6 dB apart; `pst\level` does **not** move
  `RelativeLufs`; each unmodeled condition (comp ON, reverb ON, an EQ band ≠ `def`) raises its flag.
- **`Namager.App.Tests`** — the Level block is `Blocks[0]`, expanded by default, and holds exactly
  one field at `PresetLevelPath` and **not** `…\pst\tmp`; a browse response lacking the node loads
  the editor without it; editing the slider sets `IsDirty` and Save writes that path. Match
  arithmetic including ±20 saturation reporting, a cancelled picker changing nothing, a failing
  target read reporting rather than throwing, and the amp blob being read once per session rather
  than once per estimate.

## Hardware validation

`docs/HARDWARE-VALIDATION-preset-level.md` (VoidX-Control closed throughout):

1. Connect, select a preset: Level is the top section, expanded, volume icon present, slider 0.0 dB.
2. Drag to −6 dB, Save, reselect — the slider reads −6.0 and the preset is audibly quieter. Dump the
   slot with HwCheck and diff against a `docs/backups/` copy: only the `…\pst\level` line differs.
3. The reset button returns it to 0.0.
4. Match against a conspicuously louder preset: the proposed trim is the right sign and roughly the
   right size; after Save, A/B shows the jump gone.
5. Spot-check a flagged (comp/reverb ON) preset to see where the estimate is weakest.
6. `…\pst\tmp` appears nowhere in the UI.

## Known limits (stated in the UI and the doc, not papered over)

- The `amp\vol` taper is an assumption until it is calibrated against the device VU meters.
- Compressor, gate and wet effects are not modeled — affected presets are flagged, not silently
  mis-trimmed.
- ±20 dB is a hard clamp; a pair more than 40 dB apart cannot be fully matched.
- This is a static estimate on a fixed drive signal, not a measurement of the user's playing.
- The VU meter stream (`root\sys\_meters\_out0/_out1`, filtered at
  `src/Sonulab.Core/Protocol/ResponseParser.cs:27`) and the `root\usb\mode` audio path are
  unexplored; either could later upgrade this from predicted to measured.
