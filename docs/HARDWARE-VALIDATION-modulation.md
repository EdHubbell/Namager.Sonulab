# Hardware validation — Modulation block + recursive nesting

Device: StompStation "AMP Station", fw 2.5.1, USB (VoidX-Control CLOSED).
**Back up first:** the app's backup button, or `dotnet run --project tools/HwCheck` for a read-only
sanity check. Backups land in `docs/backups/` (gitignored).

## Structure

- [ ] Modulation appears between Impulse Response and Delay.
- [ ] Its header carries the power glyph, lit per `mod\on_off`.
- [ ] Expanding it shows, in order: Enable, Mode, ▸ Rate, Depth, Dry-Wet, ▸ Tone and Character, ▸ Tremolo.
- [ ] Tremolo has its own power glyph and its own ▸ Rate inside it.
- [ ] Rate (both of them) holds Rate, Time Subdivision, Lock Options — and nothing else.
- [ ] The two Rate groups expand independently; collapsing one does not move the other.
- [ ] Select a preset with Tremolo ON: Tremolo is already expanded when you open Modulation.
      Select one with it OFF: collapsed.
- [ ] Collapse an auto-opened Tremolo, switch presets, return — it stays collapsed.

## Round-trip (one parameter at a time, Save between each)

For each of `mode`, `dpth`, `mix`, `rate\rawdata`, `rate\sbdv`, `rate\lock`, `tcfolder\emp`,
`tcfolder\shape`, `tcfolder\hicut`, `tcfolder\locut`, `tcfolder\sphase`, `trfolder\on_off`,
`trfolder\dpt`, `trfolder\wave`, `trfolder\sphase`, `trfolder\rate\rawdata`, `trfolder\rate\sbdv`,
`trfolder\rate\lock`:

- [ ] Change it, Save, select another preset, come back — the value persisted.
- [ ] Confirm against the device: `dotnet run --project tools/HwCheck -- --browse root\app\mod`.

## Audible

- [ ] `mod\on_off` ON with Mode = Chorus: Depth and Dry-Wet audibly change the effect.
- [ ] Mode = Flanger and Mode = Phaser each sound distinct.
- [ ] Rate `rawdata` changes the sweep speed; `sbdv` does so in tempo-synced steps.
- [ ] Tremolo ON: its Depth, Waveform and its own Rate act independently of the parent block.
- [ ] Rate `lock` = Global / Preset behaves sanely against the pedal tempo (observe only — this
      cycle does not design around tempo lock).

## Readout

- [ ] Hi-Cut reads `18000 Hz`, not `18000.00`. Modulation Depth (`mod\dpth`) reads `50%`. Tremolo Depth (`trfolder\dpt`) reads `25%`. Stereo Phase reads `0 deg`.
- [ ] Delay time reads `300 ms`; gate threshold reads `-60 dB`.
- [ ] At the narrowest usable window width, the widened value column (52 → 64 px) has not pushed the reset button out of view.

## No regressions elsewhere

- [ ] Delay renders with Time / Tone and Character / Modulation / Dual Delay as expanders, Time
      second, and every field it had before is still present and still saves.
- [ ] Dual Delay's own Time R group is nested inside it, not flattened alongside its leaves.
- [ ] Expression's Wah and Volume folders still render and save.
- [ ] The Level block is still first, still expanded, still explains itself, and match-volume still
      opens the preset picker and applies a proposal.
- [ ] The amp picker's detail flyout still opens from the Amp block.
