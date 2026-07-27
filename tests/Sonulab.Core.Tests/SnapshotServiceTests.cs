using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

/// <summary>SnapshotService reads presets, amps and IRs off three separate fake devices (the real
/// pedal is one device, but PROTOCOL.md models presets/amps/IRs as distinct lists; the codebase's
/// existing fakes mirror that split — see FakePresetDevice, FakeAmpDevice, FakeIrDevice) and writes
/// a .namsnap via SnapshotArchive. Read-only: no test here asserts any device write.</summary>
public class SnapshotServiceTests
{
    private static byte[] AmpBlob(byte fill) => Enumerable.Repeat(fill, 12288).ToArray();
    private static byte[] IrBlob(byte fill) => Enumerable.Repeat(fill, 4096).ToArray();

    private sealed record Fixture(
        SnapshotService Service, FakePresetDevice Presets, FakeAmpDevice Amps, FakeIrDevice Irs);

    /// <summary>Builds the service over three independent fake devices, following the construction
    /// pattern in AmpServiceTests/IrServiceTests. paceMs/settleMs are 0 to keep tests fast.</summary>
    private static Fixture BuildSnapshotService()
    {
        var presetDev = new FakePresetDevice();
        var ampDev = new FakeAmpDevice();
        var irDev = new FakeIrDevice();
        presetDev.OpenAsync().GetAwaiter().GetResult();
        ampDev.OpenAsync().GetAwaiter().GetResult();
        irDev.OpenAsync().GetAwaiter().GetResult();

        var svc = new SnapshotService(
            new DeviceRepository(new SonuClient(presetDev)),
            new AmpService(new SonuClient(ampDev), backupDir: Path.Combine(Path.GetTempPath(), $"snap-amp-{Guid.NewGuid():N}"), paceMs: 0, settleMs: 0),
            new IrService(new SonuClient(irDev), backupDir: Path.Combine(Path.GetTempPath(), $"snap-ir-{Guid.NewGuid():N}"), paceMs: 0, settleMs: 0));

        return new Fixture(svc, presetDev, ampDev, irDev);
    }

    [Fact]
    public async Task Captures_every_occupied_slot_and_skips_empty_ones()
    {
        var f = BuildSnapshotService();
        // 2 presets, 1 amp, 1 IR occupied; the rest of each list left empty.
        f.Presets.SeedSlot(0, "PresetA", new[] { $@"root\app\amp\amp:{{""value"":""mA""}}" });
        f.Presets.SeedSlot(1, "PresetB", new[] { $@"root\app\amp\amp:{{""value"":""mB""}}" });
        f.Amps.SeedAmp(0, "AmpA", AmpBlob(0x11));
        f.Irs.SeedIr(0, "IrA", IrBlob(0x22));

        using var ms = new MemoryStream();
        var manifest = await f.Service.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                                                      "0.9.7", "2026-07-26T14:02:11Z");

        Assert.Equal(4, manifest.Slots.Count);
        Assert.All(manifest.Slots, s => Assert.Equal(64, s.Sha.Length));

        ms.Position = 0;
        var (readBack, blobs) = SnapshotArchive.Read(ms);
        Assert.Equal(manifest.Slots.Count, blobs.Count);
        Assert.Equal("2026-07-26T14:02:11Z", readBack.CreatedUtc);
    }

    [Fact]
    public async Task Amp_identity_is_null_until_amps_carry_Tone3000_ids()
    {
        var f = BuildSnapshotService();
        f.Amps.SeedAmp(0, "AmpA", AmpBlob(0x33));
        f.Amps.SeedAmp(1, "AmpB", AmpBlob(0x44));

        using var ms = new MemoryStream();
        var manifest = await f.Service.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                                                      "0.9.7", "2026-07-26T14:02:11Z");

        // Scope decision, not an absence of data — see the task-6 brief. Asserted so that whoever
        // populates amp identity later has to come here and change it deliberately.
        var ampSlots = manifest.Slots.Where(s => s.Kind == SnapshotSlotKind.Amp).ToList();
        Assert.Equal(2, ampSlots.Count);
        Assert.All(ampSlots, s => Assert.Null(s.T3k));
    }

    [Fact]
    public async Task Ir_identity_comes_from_the_supplied_resolver()
    {
        var f = BuildSnapshotService();
        f.Irs.SeedIr(0, "IrA", IrBlob(0x55));

        using var ms = new MemoryStream();
        var manifest = await f.Service.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
            "0.9.7", "2026-07-26T14:02:11Z",
            resolveIrIdentity: _ => new SnapshotT3k(2468, 1357));

        var ir = manifest.Slots.First(s => s.Kind == SnapshotSlotKind.Ir);
        Assert.Equal(2468, ir.T3k!.ToneId);
        Assert.Equal(1357, ir.T3k!.ModelId);
    }

    [Fact]
    public async Task Cancellation_mid_capture_leaves_no_partial_file_claim()
    {
        var f = BuildSnapshotService();
        f.Presets.SeedSlot(0, "PresetA", new[] { $@"root\app\amp\amp:{{""value"":""mA""}}" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var ms = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Service.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"),
                                    "0.9.7", "2026-07-26T14:02:11Z", ct: cts.Token));

        // The name promises no partial file: CaptureAsync never writes to the destination stream
        // itself (SnapshotArchive.Write is only called by the caller after CaptureAsync returns a
        // manifest), so a pre-cancelled token must leave the destination untouched.
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task Reports_progress_for_each_slot_read()
    {
        var f = BuildSnapshotService();
        f.Presets.SeedSlot(0, "PresetA", new[] { $@"root\app\amp\amp:{{""value"":""mA""}}" });
        f.Amps.SeedAmp(0, "AmpA", AmpBlob(0x66));
        f.Irs.SeedIr(0, "IrA", IrBlob(0x77));
        var seen = new List<SnapshotCaptureProgress>();

        using var ms = new MemoryStream();
        await f.Service.CaptureAsync(ms, new SnapshotDevice("StompStation", "2.5.1"), "0.9.7",
                                      "2026-07-26T14:02:11Z",
                                      progress: new SyncProgress<SnapshotCaptureProgress>(seen.Add));

        Assert.NotEmpty(seen);
        Assert.Equal(seen.Count, seen.Last().Done);
        Assert.Equal(3, seen.Last().Total);
    }
}

/// <summary>Reports synchronously on the calling thread — Progress&lt;T&gt; posts to a
/// SynchronizationContext (thread-pool queued when none is ambient, as in xUnit tests), which races
/// with assertions made right after the awaited call returns. Mirrors AmpServiceTests' SyncProgress.</summary>
file sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
