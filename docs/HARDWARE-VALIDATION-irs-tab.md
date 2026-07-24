# Hardware validation — IRs tab reorder (Cycle 2)

Manual checks requiring the pedal (VoidX-Control CLOSED; app via `dotnet run --project src/Namager.App`).

- [ ] **Reorder**: select an IR, click Move Up/Down (toolbar) and the per-row up/down buttons;
      confirm the slot order changes on the pedal, ~120 ms/step, names+content intact.
- [ ] **Reorder a referenced IR**: reorder an IR that a preset uses; confirm it is NOT blocked,
      the move succeeds, and the preset still resolves its IR (name unchanged).
- [ ] **No highlight rescan**: the "used in presets" highlights do not blank/reflow after a reorder.
