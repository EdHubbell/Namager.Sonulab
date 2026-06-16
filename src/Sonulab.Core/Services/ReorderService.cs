using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService
{
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
        // Collision guard: our temporary slot names use TempPrefix. If a real preset already uses it,
        // save-by-name could land in the wrong slot — refuse rather than risk corruption.
        if (slots.Any(s => s.Name.StartsWith(TempPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"A preset name uses the reserved reorder prefix '{TempPrefix}'; rename it before reordering.");
        var occupants = new int[slots.Count];
        for (int i = 0; i < slots.Count; i++) occupants[i] = slots[i].IsEmpty ? -1 : i;

        var target = SlotPlanner.Move(occupants, from, to);
        var (min, max) = SlotPlanner.ChangedRange(from, to);

        // Snapshot the affected occupied slots (name + content) for both the write source and rollback.
        var snap = new Dictionary<int, (string Name, PresetDocument Doc)>();
        for (int i = min; i <= max; i++)
            if (occupants[i] != -1)
                snap[i] = (slots[i].Name, await _repo.ReadPresetAsync(i, ct));

        try
        {
            await WriteRangeAsync(target, snap, min, max, progress, ct);
        }
        catch (Exception original)
        {
            try
            {
                await WriteRangeAsync(occupants, snap, min, max, null, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                throw new AggregateException(
                    "Reorder failed and rollback also failed; the device may be in an inconsistent state.",
                    original, rollbackEx);
            }
            throw;
        }
    }

    // Writes `arrangement[slot]` content into each slot in [min,max] using temporary unique names,
    // then sets the final names. arrangement[slot] is -1 (empty) or a snapshot key (source slot id).
    private async Task WriteRangeAsync(
        int[] arrangement, Dictionary<int, (string Name, PresetDocument Doc)> snap,
        int min, int max, IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        int total = (max - min + 1) * 2;
        int done = 0;

        // Phase 1: content under temporary unique names (or delete for empties).
        for (int slot = min; slot <= max; slot++)
        {
            ct.ThrowIfCancellationRequested();
            int src = arrangement[slot];
            if (src == -1)
            {
                await _repo.DeleteAsync(slot, ct);
            }
            else
            {
                var (_, doc) = snap[src];
                await _repo.WritePresetToSlotAsync(slot, TempName(slot), doc, verify: true, ct);
            }
            progress?.Report(new ReorderProgress(++done, total, src == -1 ? $"slot {slot + 1}: clear" : $"slot {slot + 1}: content"));
        }

        // Phase 2: final names (name-only; never triggers save-by-name, so duplicates are impossible here).
        for (int slot = min; slot <= max; slot++)
        {
            ct.ThrowIfCancellationRequested();
            int src = arrangement[slot];
            if (src != -1)
                await _repo.RenameAsync(slot, snap[src].Name, ct);
            progress?.Report(new ReorderProgress(++done, total, src == -1 ? $"slot {slot + 1}: (empty)" : $"slot {slot + 1}: name"));
        }
    }

    // Short temp names (the device caps names at ~31 chars; a GUID suffix would not round-trip).
    // Unique per slot within the affected range; the collision guard in MoveAsync ensures no real
    // preset uses this prefix.
    private const string TempPrefix = "__sstmp_";
    private static string TempName(int slot) => $"{TempPrefix}{slot}";
}
