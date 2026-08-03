using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class SnapshotRestoreException(string message) : Exception(message);

public enum RestoreAction { Write, Clear }

/// <summary>One slot's planned operation. PedalOccupied is captured at plan time so execute
/// never dreads an empty slot (re-verified fresh at execute time too — see ExecuteAsync).
/// PedalName is the pedal's name at plan time (null when unoccupied) — rename on this device
/// is a name-table-only write, so a Write action can find its bytes already identical while its
/// name still differs; ExecuteAsync compares PedalName against Name to catch that case.</summary>
public sealed record RestoreSlotAction(
    SnapshotSlotKind Kind, int Index, string Name, RestoreAction Action, bool PedalOccupied, string? PedalName);

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
                    actions.Add(new RestoreSlotAction(kind, i, snap.Name, RestoreAction.Write, occupied,
                        occupied ? pedal[i].Name : null));
                else if (occupied)
                    actions.Add(new RestoreSlotAction(kind, i, pedal[i].Name, RestoreAction.Clear, true, pedal[i].Name));
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

    /// <summary>Applies a plan: exact mirror. Writes overwrite (or land, for a previously-empty
    /// slot); slots the snapshot didn't occupy are cleared. currentBlobs (from a prior safety
    /// snapshot read) lets the caller skip a redundant compare dread for the slots it covers —
    /// the dict may be PARTIAL, and a missing key falls back to this method reading the slot
    /// itself (archiving it as a "-prerestore" file), exactly as if currentBlobs were absent.
    /// Occupancy AND name are re-verified fresh immediately before every such self-read (never
    /// trusted from plan-time PedalOccupied/PedalName, which can go stale on a multi-minute
    /// restore — e.g. a front-panel rename or delete mid-run): this NEVER dreads a slot that is
    /// empty right now, and the skip-path rename decision is judged against the CURRENT name,
    /// not the plan-time one, so an out-of-band rename between PlanAsync and this action is
    /// still corrected. currentBlobs entries carry no such freshness guarantee of their own —
    /// callers must read them immediately before calling ExecuteAsync (Task 5/6's caller reads
    /// the safety snapshot right before execution); a stale dict can skip a rename or resurrect
    /// an out-of-band-emptied slot, since a currentBlobs hit skips the fresh read entirely. Every
    /// device call inside an action runs on CancellationToken.None: an abandoned staged burst
    /// mid-slot is the one shape the write discipline can't make safe, so cancellation only
    /// lands BETWEEN actions.</summary>
    public async Task<RestoreResult> ExecuteAsync(
        SnapshotRestorePlan plan,
        IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]>? currentBlobs = null,
        IProgress<SnapshotRestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        int total = plan.Actions.Count, done = 0;
        int written = 0, skipped = 0, cleared = 0;
        foreach (var a in plan.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var svc = ServiceFor(a.Kind);
            if (a.Action == RestoreAction.Write)
            {
                var snapBlob = plan.Blobs[(a.Kind, a.Index)];
                byte[]? current = null;
                bool haveCurrent = currentBlobs is not null &&
                                   currentBlobs.TryGetValue((a.Kind, a.Index), out current);
                // Name to judge the rename-on-skip decision against: plan-time PedalName on the
                // currentBlobs-hit path (no fresh read happens there — see the doc above), but
                // the just-read CURRENT name whenever this method does its own self-read, so an
                // out-of-band rename between PlanAsync and this action is still caught.
                string? pedalName = a.PedalName;
                if (!haveCurrent)
                {
                    pedalName = await CurrentNameAsync(svc, a.Index);
                    if (pedalName is not null)
                    {
                        progress?.Report(new(a.Kind, RestoreSlotPhase.Comparing, done, total, a.Name));
                        current = await svc.ReadAndArchiveAsync(a.Index, "-prerestore", CancellationToken.None);
                    }
                }
                if (current is not null && current.AsSpan().SequenceEqual(snapBlob))
                {
                    // Bytes already match, but rename on this device is a name-table-only write —
                    // a slot can be "content identical, name stale" (e.g. renamed since the
                    // snapshot was taken, or out-of-band since PlanAsync). Presets reference
                    // amps/IRs BY NAME, so leaving a stale name here would silently orphan every
                    // restored preset that names this slot. Content matched, so this still counts
                    // as SkippedIdentical.
                    if (pedalName != a.Name)
                        await svc.RenameAsync(a.Index, a.Name, CancellationToken.None);
                    skipped++;
                }
                else
                {
                    progress?.Report(new(a.Kind, RestoreSlotPhase.Writing, done, total, a.Name));
                    await svc.UploadAsync(a.Index, snapBlob, a.Name,
                                          progress: null, skipBackup: true, ct: CancellationToken.None);
                    written++;
                }
            }
            else
            {
                progress?.Report(new(a.Kind, RestoreSlotPhase.Clearing, done, total, a.Name));
                bool haveCurrent = currentBlobs is not null && currentBlobs.ContainsKey((a.Kind, a.Index));
                // Same TOCTOU concern as the Write branch above: only self-read if the slot is
                // occupied RIGHT NOW. DeleteAsync itself already no-ops safely on an empty slot
                // (one cheap list read, no dwrite), so it still runs unconditionally below —
                // only the archive dread is skipped when there's nothing left to archive.
                if (!haveCurrent && await CurrentNameAsync(svc, a.Index) is not null)
                    await svc.ReadAndArchiveAsync(a.Index, "-prerestore", CancellationToken.None);
                await svc.DeleteAsync(a.Index, CancellationToken.None);
                cleared++;
            }
            done++;
            progress?.Report(new(a.Kind,
                a.Action == RestoreAction.Clear ? RestoreSlotPhase.Clearing : RestoreSlotPhase.Writing,
                done, total, a.Name));
        }
        return new RestoreResult(written, skipped, cleared);
    }

    /// <summary>Fresh name-table read for one slot, returning null when the slot is empty right
    /// now. Used immediately before a self-read: as the occupancy gate (non-null = safe to
    /// dread), and — on the Write path — as the up-to-date name for the rename-on-skip decision,
    /// so a slot renamed out-of-band between PlanAsync and this action isn't judged against a
    /// stale plan-time name.</summary>
    private static async Task<string?> CurrentNameAsync(SlotBlobService svc, int index)
    {
        var pedal = await svc.ListAsync(CancellationToken.None);
        var name = pedal[index].Name;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private SlotBlobService ServiceFor(SnapshotSlotKind kind) => kind switch
    {
        SnapshotSlotKind.Preset => presets,
        SnapshotSlotKind.Amp => amps,
        _ => irs,
    };
}
