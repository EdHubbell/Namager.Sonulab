# Hardware validation — Preset Level

> Status: **not yet run.** No agent can run these — the parameter editor needs a physical pedal
> connected to populate, and the two new icon geometries (`Icon.VolumeHigh`, `Icon.VolumeEqual`)
> have never been rendered by anything. This doc is where a human checks both. Run top to bottom
> with **VoidX-Control CLOSED** throughout — it holds COM6 exclusively and the app cannot open the
> port while it runs.

Surfaces the pedal's per-preset output trim (`root\app\output\pst\level`, "Preset Level",
−20…+20 dB) as the top block of the parameter editor, plus a "match this preset's volume to
another" action backed by an offline K-weighted loudness estimate (`Sonulab.Distill.Loudness` /
`LevelModel`). See `docs/superpowers/specs/2026-08-03-preset-level-design.md` for the design.

- [ ] **1. Level block on load.** Connect (VoidX-Control closed), select a preset.
      **Expect:** `Level` is the top section of the parameter editor, expanded by default, with the
      `Icon.VolumeHigh` glyph rendered in the header and a slider spanning −20…+20 reading 0.0 dB
      (assuming an unedited preset — every shipped `.pst` in `presets/` reads 0.000000 today).
- [ ] **2. Edit and persist.** Drag the slider to −6 dB, press Save, select a different preset, then
      select the first one again. **Expect:** the slider reads −6.0 dB and the preset is audibly
      quieter than before.
- [ ] **3. Byte-level diff.** Before step 2 (or from a prior backup under `docs/backups/`), have a
      copy of the slot. After step 2, `dotnet run --project tools/HwCheck` to dump the slot (or use
      the existing dump/diff flow) and diff against the pre-edit copy. **Expect:** only the
      `root\app\output\pst\level` line differs.
- [ ] **4. Reset button.** With the −6 dB edit still loaded, press the field's reset button.
      **Expect:** the slider returns to 0.0 dB and the volume glyph in the header un-highlights
      (the "changed from default" indicator clears).
- [ ] **5. Match, louder target, no write.** Press match, choose a conspicuously louder preset from
      the picker. **Expect:** the proposed trim has the right sign (negative, if the target is
      louder) and a plausible size; the status bar names any "check by ear" caveats for either side;
      nothing is written to the device yet (the slider is only dirtied).
- [ ] **6. Match, then apply.** From step 5, press Save, then A/B the two presets by ear.
      **Expect:** the volume jump between them is gone or much reduced.
- [ ] **7. Match against a flagged preset.** Repeat step 5 targeting a preset that has the
      compressor or reverb on. **Expect:** the status bar's caveat list flags it, and this is the
      case where the estimate is expected to be least accurate — record the by-ear error in dB
      here: ______
- [ ] **8. No BPM control.** Search the whole UI for "Preset TEMPO" or any BPM control tied to
      `root\app\output\pst\tmp`. **Expect:** absent everywhere (it is deliberately not surfaced).
- [ ] **9. Amp-blob memoization timing.** Press match a second time in the same session against the
      *same* target used before. **Expect:** noticeably faster than the first match — the amp blob
      is memoized per view-model instance, so the ~3 s 96-chunk read happens once per session, not
      once per match. Record both durations: first ______ s, second ______ s.

## Known limits (see spec for detail)

- The `amp\vol` %→dB taper is an assumption, not calibrated against device VU meters.
- Compressor, gate, and wet effects (delay, reverb, mod) are not modeled — affected presets are
  flagged rather than silently mis-trimmed.
- ±20 dB is a hard clamp; a pair more than 40 dB apart cannot be fully matched in one step.
- This is a static estimate on a fixed drive signal, not a measurement of the user's own playing.
- Matching now reads the loaded preset's own `.pst` in addition to the target's (see spec
  "Known limits"), so each match costs roughly two preset reads plus one amp read (cached per
  session after the first) — not one preset read as originally scoped.
