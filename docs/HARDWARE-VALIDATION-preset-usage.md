# Hardware / Visual Validation — Preset-usage highlighting and guards

Run with the pedal connected (USB, VoidX-Control CLOSED). Check each item.

## Checklist

- [ ] 1. Open the Amps tab. Amps referenced by an active preset show their name in the amber accent color, SemiBold weight. Unused amps display with normal text style. Verify the highlight is visible and consistent.

- [ ] 2. Repeat step 1 on the IR tab. IR slots referenced by an active preset show their name in amber accent, SemiBold. Unused IRs show normal text style.

- [ ] 3. In the Amps tab, select an amp that is referenced by a preset. Hover over its row — a tooltip appears showing "Used in: <preset names>". Verify the tooltip is readable and lists all presets using that amp.

- [ ] 4. Repeat step 3 on the IR tab. Hovering a used IR shows "Used in: <preset names>".

- [ ] 5. In the Amps tab, select a used amp and click the Delete button (or use the delete command). The delete is prevented; the wrapped error message appears above the list showing the preset name(s) blocking the delete. No amp is deleted.

- [ ] 6. Repeat step 5 on the IR tab. Attempting to delete a used IR is blocked with the error message listing the referencing preset(s).

- [ ] 7. In the Amps tab, select a used amp. Press F2 or use the context menu Rename option. The rename is prevented; the wrapped error message appears showing the blocking preset name(s). The row remains unchanged.

- [ ] 8. Repeat step 7 on the IR tab. Attempting to rename a used IR is blocked, with the error message visible.

- [ ] 9. Select an unused amp in the Amps tab. Delete it — the delete succeeds (after backup). No error message appears.

- [ ] 10. Select an unused IR in the IR tab. Rename it (F2) — the rename succeeds. No error message appears.

- [ ] 11. Return to the Presets tab. Select a preset that uses a specific amp and IR. Delete that preset (after confirming the backup). Return to the Amps and IR tabs — the highlighting and tooltips update to reflect the removal. The previously used items are now displayed as unused (normal text style).

- [ ] 12. Check both light and dark themes (Windows Settings > Personalization > Colors). The amber accent highlight and danger red error message are readable and intentional in both variants.
