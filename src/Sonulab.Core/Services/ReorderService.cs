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

    private static string TempName(int slot) => $"__sstmp_{slot}_{Guid.NewGuid():N}";
}
