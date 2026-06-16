using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService
{
    private const string TempPrefix = "__sstmp_";
    private readonly DeviceRepository _repo;
    public ReorderService(DeviceRepository repo) => _repo = repo;

    public async Task MoveAsync(int from, int to, IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        if (to < 0 || to >= slots.Count) throw new ArgumentOutOfRangeException(nameof(to));
        if (from == to) return;
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
        if (slots.Any(s => s.Name.StartsWith(TempPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"A preset name uses the reserved prefix '{TempPrefix}'; rename it before reordering.");

        var (min, max) = SlotPlanner.ChangedRange(from, to);
        var origName = slots.Select(s => s.Name).ToArray();

        // Backup the affected range for rollback.
        var backup = new Dictionary<int, (string Name, PresetDocument Doc)>();
        for (int i = min; i <= max; i++)
            if (!slots[i].IsEmpty) backup[i] = (origName[i], await _repo.ReadPresetAsync(i, ct));

        // Temp slot: an empty slot OUTSIDE the affected range.
        int temp = -1;
        for (int i = 0; i < slots.Count; i++)
            if ((i < min || i > max) && slots[i].IsEmpty) { temp = i; break; }

        // Fix 1: fast path is only valid when no interior slot (other than `from`) is empty.
        bool rangeHasEmpty = false;
        for (int i = min; i <= max; i++)
            if (i != from && slots[i].IsEmpty) { rangeHasEmpty = true; break; }

        try
        {
            if (temp >= 0 && !rangeHasEmpty)
                await RotateViaSelectSaveAsync(origName, from, to, min, max, temp, progress, ct);
            else
                await WriteRangeViaReplayAsync(origName, backup, from, to, min, max, progress, ct);
        }
        catch (Exception original)
        {
            // Fix 2: delete temp slot (holds only a staged copy; original is in backup).
            if (temp >= 0) { try { await _repo.DeleteAsync(temp, CancellationToken.None); } catch { /* best effort */ } }
            // Fix 3: rollback uses CancellationToken.None so a cancelled op still cleans up.
            try { await RestoreRangeAsync(origName, backup, min, max, CancellationToken.None); }
            catch (Exception rb)
            {
                throw new AggregateException("Reorder failed and rollback also failed; device may be inconsistent.", original, rb);
            }
            throw;
        }
    }

    /// <summary>
    /// Move a single preset one physical slot up or down. Occupied neighbour → adjacent
    /// swap (3-copy fast path via <see cref="MoveAsync"/>); empty neighbour → 1-copy relocate.
    /// A move past the first/last slot is a no-op.
    /// </summary>
    public async Task MoveStepAsync(int from, bool up,
        IProgress<ReorderProgress>? progress = null, CancellationToken ct = default)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        if (from < 0 || from >= slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        int to = up ? from - 1 : from + 1;
        if (to < 0 || to >= slots.Count) return;                 // at a boundary: nothing to do
        if (slots[from].IsEmpty) throw new InvalidOperationException($"Slot {from} is empty; nothing to move.");
        if (slots.Any(s => s.Name.StartsWith(TempPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"A preset name uses the reserved prefix '{TempPrefix}'; rename it before reordering.");

        if (!slots[to].IsEmpty)
        {
            await MoveAsync(from, to, progress, ct);             // occupied neighbour: adjacent swap
            return;
        }

        await RelocateToEmptyAsync(slots[from].Name, from, to, progress, ct);   // empty neighbour
    }

    // TEMPORARY STUB — replaced with the real implementation in a later task.
    private Task RelocateToEmptyAsync(string origName, int from, int to,
        IProgress<ReorderProgress>? progress, CancellationToken ct) =>
        throw new NotImplementedException();

    // FAST PATH: rotate [min,max] in place using select+save and one temp slot.
    private async Task RotateViaSelectSaveAsync(
        string[] origName, int from, int to, int min, int max, int temp,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        int span = max - min;          // number of shifts
        int total = span + 2; int done = 0;
        string Stage = TempPrefix + "stage";
        string Dst(int k) => TempPrefix + "d" + k;

        async Task Move(string sourceName, int destSlot, string destName)
        {
            ct.ThrowIfCancellationRequested();
            await _repo.SelectPresetAsync(sourceName, ct);    // load source content into live
            await _repo.RenameAsync(destSlot, destName, ct);  // name the dest so save targets it
            await _repo.SaveCurrentAsAsync(destName, ct);     // device copies content into destSlot
        }

        if (from > to)   // moving up
        {
            await Move(origName[from], temp, Stage); progress?.Report(new ReorderProgress(++done, total, "stage"));
            for (int k = from; k > to; k--) { await Move(origName[k - 1], k, Dst(k)); progress?.Report(new ReorderProgress(++done, total, $"slot {k + 1}")); }
            await Move(Stage, to, Dst(to)); progress?.Report(new ReorderProgress(++done, total, $"slot {to + 1}"));
            await _repo.DeleteAsync(temp, ct);
            await _repo.RenameAsync(to, origName[from], ct);
            for (int k = to + 1; k <= from; k++) await _repo.RenameAsync(k, origName[k - 1], ct);
        }
        else             // moving down
        {
            await Move(origName[from], temp, Stage); progress?.Report(new ReorderProgress(++done, total, "stage"));
            for (int k = from; k < to; k++) { await Move(origName[k + 1], k, Dst(k)); progress?.Report(new ReorderProgress(++done, total, $"slot {k + 1}")); }
            await Move(Stage, to, Dst(to)); progress?.Report(new ReorderProgress(++done, total, $"slot {to + 1}"));
            await _repo.DeleteAsync(temp, ct);
            await _repo.RenameAsync(to, origName[from], ct);
            for (int k = from; k < to; k++) await _repo.RenameAsync(k, origName[k + 1], ct);
        }
    }

    // FALLBACK (no temp slot): the proven param-replay write-to-slot, snapshot-based + temp names.
    private async Task WriteRangeViaReplayAsync(
        string[] origName, Dictionary<int, (string Name, PresetDocument Doc)> backup,
        int from, int to, int min, int max, IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        var occupants = new int[origName.Length];
        for (int i = 0; i < origName.Length; i++) occupants[i] = string.IsNullOrEmpty(origName[i]) ? -1 : i;
        var target = SlotPlanner.Move(occupants, from, to);
        int total = (max - min + 1) * 2; int done = 0;
        for (int s = min; s <= max; s++)
        {
            int src = target[s];
            if (src == -1) await _repo.DeleteAsync(s, ct);
            else await _repo.WritePresetToSlotAsync(s, TempPrefix + s, backup[src].Doc, verify: true, ct);
            progress?.Report(new ReorderProgress(++done, total, $"slot {s + 1}: content"));
        }
        for (int s = min; s <= max; s++)
        {
            int src = target[s];
            if (src != -1) await _repo.RenameAsync(s, backup[src].Name, ct);
            progress?.Report(new ReorderProgress(++done, total, $"slot {s + 1}: name"));
        }
    }

    private async Task RestoreRangeAsync(
        string[] origName, Dictionary<int, (string Name, PresetDocument Doc)> backup, int min, int max, CancellationToken ct)
    {
        // Rewrite each affected slot to its ORIGINAL content+name from the backup (param-replay, robust).
        for (int s = min; s <= max; s++)
            if (backup.TryGetValue(s, out var b)) await _repo.WritePresetToSlotAsync(s, TempPrefix + "r" + s, b.Doc, verify: false, ct);
            else await _repo.DeleteAsync(s, ct);
        for (int s = min; s <= max; s++)
            if (backup.TryGetValue(s, out var b)) await _repo.RenameAsync(s, b.Name, ct);
    }
}
