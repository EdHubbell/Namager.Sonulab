using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core.Connection;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Drives MainWindowViewModel.ExportSnapshotAsync end-to-end against a fake device, via the
/// exact same DeviceSession/ConnectionViewModel plumbing RestorePresetsAsyncTests and DeviceLostTests
/// use to get a real, populated Connection.Repository/Client — both have private setters, so the only
/// way to reach them from a test is a real ConnectAsync() against a fake ISonuLink.
///
/// The atomicity test is the carry-forward requirement from the Task 5 review: SnapshotArchive.Write
/// is not atomic on its destination stream, so ExportSnapshotAsync must write to a temp file and
/// rename onto the user's chosen path only after the capture fully succeeds — a fault mid-export must
/// never destroy a pre-existing good backup while failing to produce a new one.</summary>
public class SnapshotExportImportTests
{
    private sealed class FixedProvider(string name, ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(link);
    }

    /// <summary>Identifies as a real pedal (fw 2.5.1 / ESP32S3 / stompstation1 — the catalog's tested
    /// combination) with empty preset/amp/IR lists, so a capture through it produces a
    /// zero-slot-but-valid snapshot.</summary>
    private class IdentifyingEmptyDevice : FakePresetDevice
    {
        public override Task<string> SendAsync(string command, CancellationToken ct = default) => command switch
        {
            @"read root\sys\_name" => Task.FromResult("root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n"),
            @"read root\sys\_id" => Task.FromResult("root\\sys\\_id:{\"value\":\"abc\"}\r\n"),
            @"read root\sys\_ver" => Task.FromResult("root\\sys\\_ver:{\"value\":\"2.5.1\"}\r\n"),
            @"read root\sys\_arch" => Task.FromResult("root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n"),
            @"read root\sys\_license" => Task.FromResult("root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n"),
            @"browse root\presets" => Task.FromResult("root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n"),
            @"browse root\amp" => Task.FromResult("root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n"),
            @"browse root\ir" => Task.FromResult("root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n"),
            // Explicit, valid empty-list answers (rather than falling through to the base fake's
            // unhandled-command ""): AmpService/IrService.List* read these via SonuClient's retrying
            // ReadListAsync, and an unanswered read would burn its full retry budget before giving up.
            @"read root\amp" => Task.FromResult("root\\amp:{\"value\":[]}\r\n"),
            @"read root\ir" => Task.FromResult("root\\ir:{\"value\":[]}\r\n"),
            _ => base.SendAsync(command, ct),
        };
    }

    /// <summary>Same identity as <see cref="IdentifyingEmptyDevice"/>, but the amp-list read throws —
    /// simulates a hardware fault partway through SnapshotService.CaptureAsync, after the connection
    /// is live but strictly before ExportSnapshotAsync ever opens the real destination path.</summary>
    private sealed class FailingAmpListDevice : IdentifyingEmptyDevice
    {
        public override Task<string> SendAsync(string command, CancellationToken ct = default) =>
            command == @"read root\amp"
                ? throw new IOException("simulated amp-list read fault")
                : base.SendAsync(command, ct);
    }

    private static (ConnectionViewModel Vm, TDevice Dev) Connected<TDevice>(TDevice dev) where TDevice : ISonuLink
    {
        dev.OpenAsync().GetAwaiter().GetResult();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", dev) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var connVm = new ConnectionViewModel(session, null, null, dispatch: a => a());
        connVm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (connVm, dev);
    }

    [Fact]
    public async Task ExportSnapshotAsync_writes_a_readable_namsnap_when_connected()
    {
        var (connVm, _) = Connected(new IdentifyingEmptyDevice());
        Assert.True(connVm.IsConnected);

        var vm = new MainWindowViewModel(null, null) { Connection = connVm };
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        try
        {
            await vm.ExportSnapshotAsync(path);

            Assert.True(File.Exists(path));
            using var fs = File.OpenRead(path);
            var (manifest, _) = SnapshotArchive.Read(fs);
            Assert.Equal(SnapshotManifest.CurrentSchema, manifest.Schema);
            Assert.Equal("2.5.1", manifest.Device.Fw);
            Assert.Empty(manifest.Slots);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportSnapshotAsync_leaves_an_existing_destination_byte_for_byte_unchanged_if_the_capture_fails()
    {
        var (connVm, _) = Connected(new FailingAmpListDevice());
        Assert.True(connVm.IsConnected);

        var vm = new MainWindowViewModel(null, null) { Connection = connVm };
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        File.WriteAllBytes(path, original);
        try
        {
            await vm.ExportSnapshotAsync(path);   // must not throw — failures route to Status, not the caller

            Assert.Equal(original, File.ReadAllBytes(path));

            // No stray temp file left beside the (untouched) destination.
            var dir = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileName(path);
            Assert.Empty(Directory.GetFiles(dir, stem + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExportSnapshotAsync_reports_failure_and_writes_nothing_when_not_connected()
    {
        var vm = new MainWindowViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        try
        {
            await vm.ExportSnapshotAsync(path);   // must not throw
            Assert.False(File.Exists(path));
            Assert.Equal(StatusKind.Error, vm.Status.Kind);
            Assert.Equal("Connect to the pedal first.", vm.Status.Message);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
