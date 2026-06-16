# Drag-Reorder — Manual Hardware Validation (DEFERRED until operator at PC, VoidX closed)

1. Fast engine on hardware: `dotnet run --project tools/HwCheck -- --reorder-test`
   — confirm move + move-back restores order; note the round-trip time (expect SECONDS now, not minutes).
2. Full-range timing: optionally extend the harness to move idx 0 -> a far slot and back; confirm
   ~seconds and order restored.
3. Drag UI: `dotnet run --project src/Sonulab.App`, Connect, drag a preset up/down — confirm the
   insertion line shows between the right two presets, and on drop the order updates (with progress).
4. No-empty fallback: only if you ever fill all 30 slots — reorder still works (slower).
Record pass/fail + observed timings.
