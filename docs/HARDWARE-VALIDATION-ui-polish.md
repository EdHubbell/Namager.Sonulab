# Visual validation — Studio-warm UI polish + Amps master-detail

**Spec:** `docs/superpowers/specs/2026-07-06-ui-polish-design.md`. Target mockup:
`.superpowers/brainstorm/14837-1783373193/content/final-look.html` (gitignored).

Most steps are device-free. Steps marked [PEDAL] need the StompStation connected
(VoidX-Control closed).

## Checklist

- [ ] 1. `dotnet run --project src/Sonulab.App` with Windows in DARK mode:
       window/nav/top bar are warm near-black (not gray-blue); accent everywhere is
       amber (nav selection bar+tint, focus rings, progress bars) — no stock blue.
- [ ] 2. Switch Windows to LIGHT mode (Settings > Personalization > Colors) with the
       app running: everything re-renders warm-paper; no white-on-white or unreadable
       text anywhere. Both variants intentional = spec's hard requirement.
- [ ] 3. Amps tab, disconnected: list left, "Select an amp to see its details."
       placeholder right. No bottom-docked panels remain.
- [ ] 4. [PEDAL] Connect. Amps tab: select an amp with metadata → details card right
       (name header + amber SLOT badge, ruled SOURCE/DISTILLED/NAM/NOTES sections,
       accent link, Edit button). Select a VoidX-era amp → "No metadata" state.
- [ ] 5. [PEDAL] Edit notes/link → edit sub-panel in the card; Save → busy → card
       refreshes. Cancel works. Budget warning appears for a huge pasted note.
- [ ] 6. [PEDAL] Upload .nam… → right panel swaps to the upload form (details hidden);
       progress; Done → panel returns to the new amp's details. Close mid-form returns
       to details/placeholder. Upload while another amp's details showing: no overlap.
- [ ] 7. Presets tab: list + parameter editor unchanged structurally; slot numbers
       muted monospace; selection amber; dirty dot warm amber; expander sub-headers
       are small-caps-style labels.
- [ ] 8. IRs tab: single column (unchanged layout); upload panel is a warm card;
       messages use warning/danger tokens.
- [ ] 9. F2 rename still works in all three lists (in-place edit box, Enter commits,
       Esc cancels) — the ListBoxItem restyle must not break the edit TextBox.
- [ ] 10. Screenshot the Amps tab in both variants; compare against the mockup;
       attach or note deviations here. Record date + pass/fail per step.
