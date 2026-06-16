using System.Diagnostics;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed record ReorderProgress(int Done, int Total, string Message);

public sealed class ReorderService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
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
        var swBackup = Stopwatch.StartNew();
        for (int i = min; i <= max; i++)
            if (!slots[i].IsEmpty) backup[i] = (origName[i], await _repo.ReadPresetAsync(i, ct));
        swBackup.Stop();
        Log.Debug("MoveAsync from={0} to={1}: backed up {2} slot(s) in {3}ms", from, to, backup.Count, swBackup.ElapsedMilliseconds);

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
            var swPath = Stopwatch.StartNew();
            bool fast = temp >= 0 && !rangeHasEmpty;
            if (fast)
                await RotateViaSelectSaveAsync(origName, from, to, min, max, temp, progress, ct);
            else
                await WriteRangeViaReplayAsync(origName, backup, from, to, min, max, progress, ct);
            swPath.Stop();
            Log.Debug("MoveAsync from={0} to={1}: {2} path took {3}ms", from, to,
                fast ? "rotate(select+save)" : "replay(param)", swPath.ElapsedMilliseconds);
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
    /// Move a single preset one physical slot up or down using lean select+save copies that read
    /// NO preset content (the slow part of <see cref="MoveAsync"/>): occupied neighbour → adjacent
    /// swap through one temp slot; empty neighbour → single-copy relocate. A move past the
    /// first/last slot is a no-op.
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

        bool occupied = !slots[to].IsEmpty;
        Log.Debug("MoveStep from={0} up={1} -> to={2} ({3})", from, up, to, occupied ? "swap" : "relocate-into-empty");
        var sw = Stopwatch.StartNew();
        if (occupied)
            await SwapAdjacentAsync(slots, from, to, progress, ct);                  // occupied neighbour
        else
            await RelocateToEmptyAsync(slots[from].Name, from, to, progress, ct);    // empty neighbour
        sw.Stop();
        Log.Info("MoveStep from={0} -> to={1} ({2}) completed in {3}ms", from, to,
            occupied ? "swap" : "relocate", sw.ElapsedMilliseconds);
    }

    // FAST PATH: move one preset into an EMPTY adjacent slot with a single select+save copy.
    // Reads NO preset content: the source survives in `from` until it is deleted (after the copy
    // exists in `to`), so recovery restores from the on-device copy rather than a host-side backup.
    private async Task RelocateToEmptyAsync(string origName, int from, int to,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        string temp = TempPrefix + "reloc";
        try
        {
            ct.ThrowIfCancellationRequested();
            await _repo.SelectPresetAsync(origName, ct);         // live = preset content
            await _repo.RenameAsync(to, temp, ct);               // give the empty slot a unique name
            await _repo.SaveCurrentAsAsync(temp, ct);            // slot `to` now holds the content
            await _repo.DeleteAsync(from, ct);                   // vacate the source (content safe in `to`)
            await _repo.RenameAsync(to, origName, ct);           // restore the preset's real name
            progress?.Report(new ReorderProgress(1, 1, $"slot {to + 1}"));
            await VerifyNamesAsync(ct, (to, origName), (from, ""));   // cheap, content-free verify
        }
        catch (Exception original)
        {
            try { await UndoRelocateAsync(origName, from, to); }
            catch (Exception rb)
            {
                throw new AggregateException("Relocate failed and rollback also failed; device may be inconsistent.", original, rb);
            }
            throw;
        }
    }

    // Restore [from = original content/name, to = empty] without reading 8 KB of content: the
    // preset survives either in `from` (if not yet deleted) or in `to` (the staged copy).
    private async Task UndoRelocateAsync(string origName, int from, int to)
    {
        var ct = CancellationToken.None;
        var slots = await _repo.ListPresetsAsync(ct);
        if (slots[from].Name != origName && !slots[to].IsEmpty)
        {
            // `from` was vacated; the content lives in `to` — copy it back via select+save.
            await _repo.SelectPresetAsync(slots[to].Name, ct);
            await _repo.RenameAsync(from, origName, ct);
            await _repo.SaveCurrentAsAsync(origName, ct);
        }
        slots = await _repo.ListPresetsAsync(ct);
        if (to != from && !slots[to].IsEmpty) await _repo.DeleteAsync(to, ct);   // drop the partial copy
    }

    // FAST PATH: swap two occupied adjacent slots via select+save through one temp slot.
    // Reads NO preset content. Needs an empty temp slot outside {from,to}; if the device is full,
    // falls back to the slower backup+replay MoveAsync. Recovery is phase-aware: before the swap's
    // point of no return the original layout is restored, after it the swap is completed.
    private async Task SwapAdjacentAsync(IReadOnlyList<PresetSlot> slots, int from, int to,
        IProgress<ReorderProgress>? progress, CancellationToken ct)
    {
        int temp = -1;
        for (int i = 0; i < slots.Count; i++)
            if (i != from && i != to && slots[i].IsEmpty) { temp = i; break; }
        if (temp < 0) { await MoveAsync(from, to, progress, ct); return; }   // device full: slow but safe

        string nameA = slots[from].Name, nameB = slots[to].Name;
        string tBak = TempPrefix + "swap_bak";   // A parked in the temp slot (stable backup of A)
        string tF = TempPrefix + "swap_f";        // B's interim name while staged in `from`
        string tT = TempPrefix + "swap_t";        // A's interim name while staged in `to`
        int phase = 0;
        try
        {
            await CopyViaSelectSaveAsync(nameA, temp, tBak, ct); phase = 1;   // temp = A
            await CopyViaSelectSaveAsync(nameB, from, tF, ct); phase = 2;     // from = B (A safe in temp)
            await CopyViaSelectSaveAsync(tBak, to, tT, ct); phase = 3;        // to = A (B safe in from) — no return
            await _repo.RenameAsync(from, nameB, ct); phase = 4;
            await _repo.RenameAsync(to, nameA, ct); phase = 5;
            await _repo.DeleteAsync(temp, ct); phase = 6;
            progress?.Report(new ReorderProgress(1, 1, $"slots {from + 1}/{to + 1}"));
            await VerifyNamesAsync(ct, (from, nameB), (to, nameA), (temp, ""));
        }
        catch (Exception original)
        {
            try
            {
                if (phase >= 3) await FinishSwapAsync(from, to, temp, nameA, nameB);   // content swapped: complete it
                else await UndoSwapAsync(from, to, temp, nameA, nameB, tBak);          // restore the original layout
            }
            catch (Exception rb)
            {
                throw new AggregateException("Swap failed and rollback also failed; device may be inconsistent.", original, rb);
            }
            if (phase >= 3) return;   // swap effectively completed during recovery
            throw;
        }
    }

    // select source content into live, name the destination uniquely, save into it. Reads no content.
    private async Task CopyViaSelectSaveAsync(string sourceName, int destSlot, string destName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _repo.SelectPresetAsync(sourceName, ct);
        await _repo.RenameAsync(destSlot, destName, ct);
        await _repo.SaveCurrentAsAsync(destName, ct);
    }

    // Past the point of no return (content already swapped): finish the names and remove the backup.
    private async Task FinishSwapAsync(int from, int to, int temp, string nameA, string nameB)
    {
        var ct = CancellationToken.None;
        var slots = await _repo.ListPresetsAsync(ct);
        if (slots[from].Name != nameB) await _repo.RenameAsync(from, nameB, ct);
        slots = await _repo.ListPresetsAsync(ct);
        if (slots[to].Name != nameA) await _repo.RenameAsync(to, nameA, ct);
        slots = await _repo.ListPresetsAsync(ct);
        if (!slots[temp].IsEmpty) await _repo.DeleteAsync(temp, ct);
    }

    // Before the point of no return: restore [from=A/nameA, to=B/nameB], temp empty, reading no content.
    // Invariants in this window: `to` still holds B's content, and A's content is safe in the temp slot.
    private async Task UndoSwapAsync(int from, int to, int temp, string nameA, string nameB, string tBak)
    {
        var ct = CancellationToken.None;
        // 1) `to` still holds B's content here — just ensure its name.
        var slots = await _repo.ListPresetsAsync(ct);
        if (slots[to].Name != nameB) await _repo.RenameAsync(to, nameB, ct);
        // 2) Restore A into `from` from the temp backup (only if `from` was already overwritten with B).
        slots = await _repo.ListPresetsAsync(ct);
        if (slots[from].Name != nameA)
        {
            await _repo.SelectPresetAsync(tBak, ct);     // temp holds A
            await _repo.RenameAsync(from, nameA, ct);
            await _repo.SaveCurrentAsAsync(nameA, ct);
        }
        // 3) Drop the temp backup.
        slots = await _repo.ListPresetsAsync(ct);
        if (!slots[temp].IsEmpty) await _repo.DeleteAsync(temp, ct);
    }

    // Cheap, content-free verify: confirm the expected names landed in the expected slots (1 command).
    private async Task VerifyNamesAsync(CancellationToken ct, params (int Slot, string Name)[] expected)
    {
        var slots = await _repo.ListPresetsAsync(ct);
        foreach (var (slot, name) in expected)
            if (slots[slot].Name != name)
                throw new InvalidOperationException($"Reorder verify failed: slot {slot + 1} is '{slots[slot].Name}', expected '{name}'.");
    }

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
