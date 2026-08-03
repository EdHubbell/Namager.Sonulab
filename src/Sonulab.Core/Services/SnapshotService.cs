using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

/// <summary><see cref="Done"/> is a GLOBAL counter across all three stages (1..Total over the
/// whole capture), not per-kind — the UI renders "file N of Total", so it must never reset when
/// the stage changes.</summary>
public sealed record SnapshotCaptureProgress(SnapshotSlotKind Stage, int Done, int Total);

/// <summary>Reads every occupied slot off the pedal and writes a .namsnap.
///
/// Read-only: this service never writes to the device. Restoring a snapshot back onto hardware is
/// a separate concern with its own consent, verification, and cancellation requirements.</summary>
public sealed class SnapshotService(DeviceRepository presets, AmpService amps, IrService irs)
{
    public async Task<SnapshotManifest> CaptureAsync(
        Stream destination, SnapshotDevice device, string appVersion, string createdUtc,
        Func<byte[], SnapshotT3k?>? resolveIrIdentity = null,
        IProgress<SnapshotCaptureProgress>? progress = null,
        CancellationToken ct = default)
    {
        var slots = new List<SnapshotSlot>();
        var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>();

        var presetList = (await presets.ListPresetsAsync(ct)).Where(p => !p.IsEmpty).ToList();
        var ampList = (await amps.ListAmpsAsync(ct)).Where(a => !a.IsEmpty).ToList();
        var irList = (await irs.ListIrsAsync(ct)).Where(i => !i.IsEmpty).ToList();
        int total = presetList.Count + ampList.Count + irList.Count, done = 0;

        foreach (var p in presetList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = (await presets.ReadPresetAsync(p.Index, ct)).ToBytes();
            blobs[(SnapshotSlotKind.Preset, p.Index)] = bytes;
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Preset, p.Index, p.Name,
                                       SnapshotArchive.ShaOf(bytes), null));
            progress?.Report(new SnapshotCaptureProgress(SnapshotSlotKind.Preset, ++done, total));
        }

        foreach (var a in ampList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await amps.ReadAmpAsync(a.Index, ct);
            blobs[(SnapshotSlotKind.Amp, a.Index)] = bytes;
            // T3k is null for amps by scope decision. SSMD does carry identity — a tone id inside
            // the url slug, and source.sha256 for the exact model — but extracting it needs either
            // slug parsing or a source-hash index. See docs/superpowers/sdd task-6 brief.
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Amp, a.Index, a.Name,
                                       SnapshotArchive.ShaOf(bytes), null));
            progress?.Report(new SnapshotCaptureProgress(SnapshotSlotKind.Amp, ++done, total));
        }

        foreach (var i in irList)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await irs.ReadIrAsync(i.Index, ct);
            blobs[(SnapshotSlotKind.Ir, i.Index)] = bytes;
            slots.Add(new SnapshotSlot(SnapshotSlotKind.Ir, i.Index, i.Name,
                                       SnapshotArchive.ShaOf(bytes),
                                       resolveIrIdentity?.Invoke(bytes)));
            progress?.Report(new SnapshotCaptureProgress(SnapshotSlotKind.Ir, ++done, total));
        }

        var manifest = new SnapshotManifest(
            SnapshotManifest.CurrentSchema, createdUtc, appVersion, device, slots);
        SnapshotArchive.Write(destination, manifest, blobs);
        return manifest;
    }
}
