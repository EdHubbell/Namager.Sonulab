using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Drives MainWindowViewModel.RestorePresetsAsync end-to-end against a fake device,
/// via the exact same DeviceSession/ConnectionViewModel plumbing DeviceLostTests uses to get a
/// real, populated Connection.Repository — Repository has a private setter, so the only way to
/// reach it from a test is a real ConnectAsync() against a fake ISonuLink, not a shortcut around
/// ConnectionViewModel.</summary>
public class RestorePresetsAsyncTests
{
    private sealed class FixedProvider(string name, ISonuLink? link) : ILinkProvider
    {
        public string Name => name;
        public Task<ISonuLink?> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(link);
    }

    /// <summary>FakePresetDevice already faithfully models the preset read/write/rename/save wire
    /// protocol; it just doesn't answer the identify + structural-preflight reads ConnectAsync
    /// issues before DeviceRepository is ever handed out. Layer those on (verbatim identity from
    /// DeviceLostTests' KillableLink: fw 2.5.1 / ESP32S3 / stompstation1, the catalog's tested
    /// combination) and fall through to the base implementation for everything else.</summary>
    private sealed class IdentifyingPresetDevice : FakePresetDevice
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
            _ => base.SendAsync(command, ct),
        };
    }

    static (ConnectionViewModel Vm, IdentifyingPresetDevice Dev) ConnectedRepo()
    {
        var dev = new IdentifyingPresetDevice();
        dev.OpenAsync().GetAwaiter().GetResult();   // DeviceSession expects an already-open link
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", dev) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var connVm = new ConnectionViewModel(session, null, null, dispatch: a => a());
        connVm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return (connVm, dev);
    }

    /// <summary>Same identify/structural preflight as <see cref="IdentifyingPresetDevice"/>, but a
    /// firmware version (9.9.9) the compatibility catalog (only 2.5.1) has never seen — connects
    /// fine (structural preflight passes, Repository gets populated) but WritesAllowed comes back
    /// false. Used to reach FIX 3's "connected, writes not verified" branch, which is otherwise
    /// unreachable from a test: Connection.Repository has a private setter, so only a real connect
    /// against a device that clears the structural check can populate it.</summary>
    private sealed class UntestedFirmwarePresetDevice : FakePresetDevice
    {
        public override Task<string> SendAsync(string command, CancellationToken ct = default) => command switch
        {
            @"read root\sys\_name" => Task.FromResult("root\\sys\\_name:{\"value\":\"AMP Station\"}\r\n"),
            @"read root\sys\_id" => Task.FromResult("root\\sys\\_id:{\"value\":\"abc\"}\r\n"),
            @"read root\sys\_ver" => Task.FromResult("root\\sys\\_ver:{\"value\":\"9.9.9\"}\r\n"),
            @"read root\sys\_arch" => Task.FromResult("root\\sys\\_arch:{\"value\":\"ESP32S3\"}\r\n"),
            @"read root\sys\_license" => Task.FromResult("root\\sys\\_license:{\"value\":\"stompstation1\"}\r\n"),
            @"browse root\presets" => Task.FromResult("root\\presets:{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":30,\"chunk\":128,\"item_type\":\"pst_pst\"}\r\n"),
            @"browse root\amp" => Task.FromResult("root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}\r\n"),
            @"browse root\ir" => Task.FromResult("root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}\r\n"),
            _ => base.SendAsync(command, ct),
        };
    }

    static ConnectionViewModel ConnectedButWritesBlocked()
    {
        var dev = new UntestedFirmwarePresetDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", dev) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var connVm = new ConnectionViewModel(session, null, null, dispatch: a => a());
        connVm.ConnectCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return connVm;
    }

    static string WritePstFile(string dir, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact] public async Task RestorePresetsAsync_returns_not_connected_summary_when_no_repo()
    {
        // Fresh VM: its own real (never-connected) Connection has Repository == null. Must not
        // throw and must not attempt any device I/O.
        var vm = new MainWindowViewModel();
        var plan = new RestorePlan(Array.Empty<RestoreItem>(), Array.Empty<string>());

        var summary = await vm.RestorePresetsAsync(plan, progress: null, CancellationToken.None);

        Assert.Equal("Not connected — nothing was restored.", summary);
    }

    // ---- FIX 3: Restore is gated on WritesAllowed, with an honest rejection message ----

    [Fact] public async Task RestorePresetsAsync_returns_a_writes_specific_message_when_connected_but_not_writable()
    {
        // Real, reachable state: connected to the pedal, but on firmware the compatibility
        // checker has not verified for writes. Must NOT be told "Not connected" — they are.
        var connVm = ConnectedButWritesBlocked();
        Assert.True(connVm.IsConnected);
        Assert.False(connVm.WritesAllowed);
        Assert.NotNull(connVm.Repository);

        var vm = new MainWindowViewModel { Connection = connVm };
        var plan = new RestorePlan(Array.Empty<RestoreItem>(), Array.Empty<string>());

        var summary = await vm.RestorePresetsAsync(plan, progress: null, CancellationToken.None);

        Assert.Equal("This firmware isn't verified for writes — nothing was restored.", summary);
    }

    [Fact] public async Task RestorePresetsAsync_writes_every_planned_slot_and_reports_the_count()
    {
        var (connVm, dev) = ConnectedRepo();
        Assert.True(connVm.IsConnected);
        Assert.True(connVm.WritesAllowed);   // fw 2.5.1/ESP32S3/stompstation1 is the tested combo
        var repo = connVm.Repository!;

        // Seed two source presets (single shared param key, so live-state replay overwrites
        // cleanly between the two restores rather than leaking a stale key from A into B).
        dev.SeedSlot(0, "Src A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "Src B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        var docA = await repo.ReadPresetAsync(0);
        var docB = await repo.ReadPresetAsync(1);

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var fileA = WritePstFile(dir, "10 - Restored A.pst", docA.ToBytes());
        var fileB = WritePstFile(dir, "11 - Restored B.pst", docB.ToBytes());
        var plan = PresetFileNaming.PlanRestore(new[] { fileA, fileB });
        Assert.Equal(2, plan.Items.Count);
        Assert.Empty(plan.Skipped);

        var vm = new MainWindowViewModel { Connection = connVm };
        var progressLines = new System.Collections.Generic.List<string>();
        var summary = await vm.RestorePresetsAsync(
            plan, progress: new Progress<string>(s => progressLines.Add(s)), CancellationToken.None);

        Assert.Equal("Restored 2 presets.", summary);
        Assert.Equal(new[] { "1/2 — Restored A", "2/2 — Restored B" }, progressLines);

        var names = await repo.ListPresetsAsync();
        Assert.Equal("Restored A", names[10].Name);
        Assert.Equal("Restored B", names[11].Name);
        // Content actually landed, not just the name: read back and compare to what was restored.
        var backA = await repo.ReadPresetAsync(10);
        Assert.True(backA.ToBytes().AsSpan().SequenceEqual(docA.ToBytes()));
    }

    [Fact] public async Task RestorePresetsAsync_with_a_pre_cancelled_token_writes_nothing()
    {
        // Regression guard for the Critical finding: a cancelled restore must not touch the
        // device at all, and specifically must never rename a slot without also writing its
        // content (the corrupt-slot failure mode). The token is checked at the TOP of the loop,
        // before the loop body's single RestoreSlotAsync call — so with an already-cancelled
        // token, that call must never happen.
        var (connVm, dev) = ConnectedRepo();
        var repo = connVm.Repository!;
        dev.SeedSlot(0, "Src", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        var doc = await repo.ReadPresetAsync(0);

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var file = WritePstFile(dir, "05 - Restored.pst", doc.ToBytes());
        var plan = PresetFileNaming.PlanRestore(new[] { file });
        Assert.Single(plan.Items);

        var vm = new MainWindowViewModel { Connection = connVm };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var summary = await vm.RestorePresetsAsync(plan, progress: null, cts.Token);

        Assert.Equal("Restored 0 presets.", summary);
        var names = await repo.ListPresetsAsync();
        Assert.Equal("", names[5].Name);   // untouched: never renamed, let alone half-written
    }
}
