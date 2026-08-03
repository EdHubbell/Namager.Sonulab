using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class SnapshotRestoreException(string message) : Exception(message);

public enum RestoreAction { Write, Clear }

/// <summary>One slot's planned operation. PedalOccupied is captured at plan time so execute
/// never dreads an empty slot.</summary>
public sealed record RestoreSlotAction(
    SnapshotSlotKind Kind, int Index, string Name, RestoreAction Action, bool PedalOccupied);

public sealed record SnapshotRestorePlan(
    SnapshotManifest Manifest,
    IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs,
    IReadOnlyList<RestoreSlotAction> Actions)
{
    public int WriteCount => Actions.Count(a => a.Action == RestoreAction.Write);
    public int ClearCount => Actions.Count(a => a.Action == RestoreAction.Clear);
}

public enum RestoreSlotPhase { Comparing, Writing, Clearing }

/// <summary>Done is operation-wide across all stages (never resets), like
/// SnapshotCaptureProgress; Total = WriteCount + ClearCount.</summary>
public sealed record SnapshotRestoreProgress(
    SnapshotSlotKind Stage, RestoreSlotPhase Phase, int Done, int Total, string SlotName);

public sealed record RestoreResult(int Written, int SkippedIdentical, int Cleared);

/// <summary>Restores a .namsnap snapshot onto the pedal: an EXACT MIRROR of the recorded
/// slots — any pedal slot the snapshot didn't occupy is cleared, not left alone — per
/// docs/superpowers/specs/2026-08-03-restore-snapshot-design.md. This class only ever reads
/// the pedal during PlanAsync; it never opens a dialog or asks anything of the user — a plan
/// is inert data, and getting the user's consent to actually apply it is entirely the
/// caller's job (ExecuteAsync, arriving in Task 3, is what writes).</summary>
public sealed class SnapshotRestoreService(
    SlotBlobService presets, SlotBlobService amps, SlotBlobService irs)
{
    public async Task<SnapshotRestorePlan> PlanAsync(
        SnapshotManifest manifest,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs,
        CancellationToken ct = default)
    {
        var actions = new List<RestoreSlotAction>();
        foreach (var (kind, svc) in ExecutionOrder())
        {
            ct.ThrowIfCancellationRequested();
            var pedal = await svc.ListAsync(ct);
            var inSnapshot = manifest.Slots.Where(s => s.Kind == kind)
                                           .ToDictionary(s => s.Index);
            for (int i = 0; i < SlotBlobService.SlotCount; i++)
            {
                bool occupied = !string.IsNullOrEmpty(pedal[i].Name);
                if (inSnapshot.TryGetValue(i, out var snap))
                    actions.Add(new RestoreSlotAction(kind, i, snap.Name, RestoreAction.Write, occupied));
                else if (occupied)
                    actions.Add(new RestoreSlotAction(kind, i, pedal[i].Name, RestoreAction.Clear, true));
            }
        }
        return new SnapshotRestorePlan(manifest, blobs, actions);
    }

    /// <summary>IRs → Amps → Presets: referenced content lands before the presets that name it,
    /// so an interrupted restore never leaves NEW presets pointing at not-yet-restored names.</summary>
    private IEnumerable<(SnapshotSlotKind Kind, SlotBlobService Svc)> ExecutionOrder()
    {
        yield return (SnapshotSlotKind.Ir, irs);
        yield return (SnapshotSlotKind.Amp, amps);
        yield return (SnapshotSlotKind.Preset, presets);
    }
}
