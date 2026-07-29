# Hardware validation — UI issues #9, #10, #12, #13, #14

Branch: `feature/ui-issues-9-14` · Spec: `docs/superpowers/specs/2026-07-28-ui-issues-9-14-design.md`

Requires: pedal on USB, **VoidX-Control CLOSED** (it holds the COM port exclusively).
No step here performs a persistent (flash) write. Selecting a preset changes what is live on the
pedal — that is expected and is what #10 and #14 exercise.

## Gate 0 — the `def` assumption behind #13 (DO THIS FIRST)

The EQ icon lights when a dial sits away from its neutral, and neutral is the firmware default
(`def`) from the browse schema. **Whether the EQ nodes actually publish `def` was never confirmed on
hardware** — VoidX-Control held COM6 during implementation and could not be closed, so no browse
dump was taken. The code falls back to neutral = 0 for any field without a `def`, so it behaves
sanely either way, but the real answer should be recorded here.

- [ ] Run `dotnet run --project tools/HwCheck -- --browse` (read-only) and find the `root\app\eq\*` nodes.
- [ ] Record whether each carries a `def`, and its value:

      | node | has `def`? | value |
      | --- | --- | --- |
      | `root\app\eq\…` |  |  |

- [ ] **Every EQ node has `def`** → the icon lights exactly when the EQ is off its firmware neutral.
      Nothing to change; tick this and move on.
- [ ] **Some EQ node has no `def`** → that node falls back to neutral = 0. Confirm 0 really is flat
      for it. If a node's neutral is non-zero and undeclared, the icon would light on every preset
      and become noise — record the true neutral here and hardcode it in
      `BlockSectionViewModel.IsAwayFromNeutral`.

## #13 — EQ icon

- [ ] Select a preset with a flat EQ: the equalizer glyph shows **muted** in the Equalizer block header.
- [ ] Move one EQ dial off neutral: the glyph turns **accent-coloured immediately**, without expanding the block.
- [ ] Return it to neutral: the glyph goes muted again.
- [ ] The glyph appears **only** on the Equalizer block.
- [ ] Blocks with an on/off toggle still show their power icon, unchanged.

## #10 — rapid preset switching (the timing-dependent one)

The unit tests prove the coalescing logic against a gated fake. Only hardware proves the real
serial timing.

- [ ] Click 6–8 presets as fast as the UI allows.
- [ ] When it settles, **all three agree**: the editor contents, the highlighted row, and what the
      pedal is actually playing. Verify the last by ear, or with
      `dotnet run --project tools/HwCheck -- --browse root\app\preset`.
- [ ] You should **not** hear the pedal walk through every preset you clicked past — intermediate
      targets are dropped, not replayed.
- [ ] Repeat immediately after connecting, while the preset-usage scan is still running: the
      background lane must not reintroduce interleaving.
- [ ] Repeat with a preset that fails to load (e.g. mid-reconnect) — the error surfaces and the
      queue still drains to the last-clicked preset.

## #9 — amp detail flyout

- [ ] In the Presets tab, click the amp button beside the amp picker: the flyout shows that amp's metadata.
- [ ] Select metadata text and copy it — it pastes correctly.
- [ ] The same copy/paste works in the Amps tab detail pane.
- [ ] Open the flyout for an amp with a long NAM field list: it scrolls **inside** the flyout and stays on-screen.
- [ ] Click away: the flyout dismisses and the editor is untouched.
- [ ] Pick an amp that sits **after an empty slot** in the amp list and confirm the SLOT number in
      the flyout matches the Amps tab. (The picker hides empty slots; resolution goes through the
      raw list. This is the regression the `Empty_slots_do_not_shift_the_resolved_slot_number` test
      guards, but it is worth one real-device confirmation.)
- [ ] Open the flyout for an amp uploaded outside NAMager: shows "No metadata", not an error.

## #14 — used-in-presets

- [ ] **Immediately after connecting** (scan incomplete), open an amp's detail: it says
      "Checking preset usage…", **NOT** "Not used by any preset."
- [ ] After the scan completes, an amp used by presets lists them in slot order, formatted "NN Name".
- [ ] An unused amp says "Not used by any preset."
- [ ] Click a listed preset: the app switches to the Presets tab, selects it, and the pedal loads it.
- [ ] The list also appears inside the #9 flyout (same control) and behaves the same there.

## #12 — Tone3000 detail

- [ ] Sign in and open the Silvertone 1484 tone
      (https://www.tone3000.com/tones/silvertone-1484-twin-twelve-70876) — the reported stress case.
- [ ] The title/label is **fully readable** — no truncation.
- [ ] Every file is reachable via the dropdown; nothing is cut off.
- [ ] Resize the window narrow and short: the detail region scrolls internally, and the page never
      scrolls horizontally.
- [ ] "Send to pedal" targets the file chosen in the dropdown (verify the slot contents afterwards).
- [ ] A tone with no A2 models still shows "No A2 models for this tone." and no dropdown.

## Regression sweep (things this branch touched indirectly)

- [ ] Amps tab: select amps up and down the list — details load, spinner behaves, no stale card.
- [ ] Amps tab: "Edit notes/link" → Save to pedal still works (the metadata cache moved into
      `AmpDetailViewModel`; this is the path that reads it back).
- [ ] Amp upload still auto-opens the detail card for the uploaded slot.
- [ ] Amp delete/rename guards still refuse an in-use amp (issue #11 is deliberately NOT implemented
      on this branch — the refusal must still be there).
