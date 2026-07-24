using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class IrListViewModelTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"ir-vm-{Guid.NewGuid():N}");
    private readonly List<string> _tempFiles = new();
    public void Dispose()
    {
        if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true);
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { } }
    }

    private (IrListViewModel vm, FakeIrDevice dev, List<string> converted) Make(bool writes = true, int seed = 2)
    {
        var dev = new FakeIrDevice();
        for (int i = 0; i < seed; i++) dev.SeedIr(i, $"Ir{i}", Enumerable.Repeat((byte)(i + 1), 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var converted = new List<string>();
        var vm = new IrListViewModel(svc, writes, convertWav: p => { converted.Add(p); return Enumerable.Repeat((byte)0xC0, 4096).ToArray(); });
        return (vm, dev, converted);
    }

    private string TempFile(string name, int bytes = 4096)
    {
        var p = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(p, Enumerable.Repeat((byte)0xEE, bytes).ToArray());
        _tempFiles.Add(p);
        return p;
    }

    [Fact] public async Task Refresh_loads_30_items()
    {
        var (vm, _, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Items.Count);
        Assert.Equal("Ir0", vm.Items[0].Name);
        Assert.True(vm.Items[5].IsEmpty);
    }

    [Fact] public async Task Wav_upload_converts_then_uploads_and_keeps_panel_open_on_done()
    {
        var (vm, dev, converted) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("SpringCab.wav"));
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Single(converted);
        Assert.Equal("SpringCab", dev.SlotNames[2]);
        Assert.Equal(0xC0, dev.SlotBlobs[2]![0]);              // converter's bytes, not the file's
        Assert.True(vm.IsUploadPanelOpen);                     // visible Done state
        Assert.StartsWith("Done", vm.UploadStatus);
        Assert.Equal(2, vm.Selected?.Index);
    }

    [Fact] public async Task Irblob_upload_skips_conversion()
    {
        var (vm, dev, converted) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("Backup.irblob"));
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Empty(converted);
        Assert.Equal(0xEE, dev.SlotBlobs[2]![0]);
    }

    [Fact] public async Task Converter_failure_surfaces_and_device_untouched()
    {
        var dev = new FakeIrDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, 0, 0);
        var vm = new IrListViewModel(svc, true, convertWav: _ => throw new InvalidDataException("not audio"));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("bad.wav"));
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Contains("not audio", vm.UploadError);
        Assert.DoesNotContain(dev.CommandLog, c => c.StartsWith("dwrite"));
    }

    [Fact] public async Task List_ops_are_gated_while_uploading_and_writes_gate_holds()
    {
        var (vmRo, devRo, _) = Make(writes: false);
        await vmRo.RefreshCommand.ExecuteAsync(null);
        vmRo.Selected = vmRo.Items[0];
        await vmRo.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Ir0", devRo.SlotNames[0]);               // gated

        var (vm, _, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.CanMutate);
        vm.BeginUploadCommand.Execute(TempFile("x.irblob"));
        Assert.True(vm.IsUploadPanelOpen);
    }

    [Fact] public async Task Duplicate_name_rejected_before_any_work()
    {
        var (vm, dev, converted) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("Ir0.wav"));    // collides with seeded slot 0
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.NotNull(vm.UploadError);
        Assert.Empty(converted);
        Assert.Null(dev.SlotNames[2]);
    }

    [Fact] public async Task No_empty_slots_blocks_with_message()
    {
        var (vm, _, _) = Make(seed: 30);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("y.wav"));
        Assert.False(vm.IsUploadPanelOpen);
        Assert.NotNull(vm.UploadBlockedMessage);
    }

    [Fact] public async Task Delete_reports_success_to_status()
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "Ir0", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var status = new FakeStatusService();
        var vm = new IrListViewModel(svc, writesAllowed: true, status: status);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Contains(status.Succeeded, m => m.Contains("Deleted"));
    }

    [Fact] public async Task Wav_upload_reports_success_to_status()
    {
        var dev = new FakeIrDevice();
        for (int i = 0; i < 2; i++) dev.SeedIr(i, $"Ir{i}", Enumerable.Repeat((byte)(i + 1), 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var converted = new List<string>();
        var status = new FakeStatusService();
        var vm = new IrListViewModel(svc, true, status: status,
            convertWav: p => { converted.Add(p); return Enumerable.Repeat((byte)0xC0, 4096).ToArray(); });
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempFile("SpringCab.wav"));
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Single(converted);
        Assert.Contains(status.Succeeded, m => m.Contains("Uploaded"));
    }

    // ---- preset-usage highlight & guards (Task 5) ----

    private (IrListViewModel vm, FakeIrDevice dev) MakeWithUsage(FakePresetUsageService usage)
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "V30", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.SeedIr(1, "Greenback", Enumerable.Repeat((byte)2, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new IrListViewModel(svc, writesAllowed: true, usage: usage), dev);
    }

    [Fact] public async Task Refresh_marks_used_irs()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, _) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.Items[0].IsUsed);                          // "V30" used by "Clean"
        Assert.False(vm.Items[1].IsUsed);                         // "Greenback" unused
    }

    [Fact] public async Task Delete_of_a_used_ir_is_blocked_with_a_message()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("V30", dev.SlotNames[0]);                   // NOT deleted
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("IR file is used", vm.ErrorMessage);
        Assert.Contains("03 Clean", vm.ErrorMessage);            // slot number + name
    }

    [Fact] public async Task Delete_of_an_unused_ir_proceeds()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];                                // "Greenback" — unused
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(dev.SlotNames[1]);
        Assert.True(vm.Items[1].IsEmpty);
    }

    [Fact] public async Task Rename_of_a_used_ir_is_blocked()
    {
        var usage = new FakePresetUsageService
        {
            Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }))
        };
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "V-30";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("V30", dev.SlotNames[0]);                   // NOT renamed
        Assert.False(item.IsEditing);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("03 Clean", vm.ErrorMessage);
    }

    [Fact] public async Task RefreshUsage_reapplies_without_relisting_irs()
    {
        var usage = new FakePresetUsageService();
        var (vm, dev) = MakeWithUsage(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.Items[0].IsUsed);
        int listReads = dev.CommandLog.Count(c => c == @"read root\ir");

        usage.Map = FakePresetUsageService.MapFor((2, "Clean", new[] { FakePresetUsageService.IrLine("V30") }));
        await vm.RefreshUsageAsync();

        Assert.True(vm.Items[0].IsUsed);
        Assert.Equal(listReads, dev.CommandLog.Count(c => c == @"read root\ir"));
    }

    // ---- progressive scan + fail-closed guards (Task 6, mirrors AmpListViewModel Task 5) ----

    private (IrListViewModel vm, FakeIrDevice dev) MakeUsageVm(FakePresetUsageService usage)
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "V30", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.SeedIr(1, "Greenback", Enumerable.Repeat((byte)2, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new IrListViewModel(svc, writesAllowed: true, usage: usage, dispatch: a => a()), dev);
    }

    [Fact]
    public async Task Refresh_does_not_block_on_an_incomplete_usage_scan()
    {
        var usage = new FakePresetUsageService { Complete = false };   // scan "running"
        var (vm, _) = MakeUsageVm(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);                                       // list is usable NOW
        Assert.NotEmpty(vm.Items);
        Assert.Equal(1, usage.EnsureScanningCount);                    // scan was kicked, not awaited
    }

    [Fact]
    public async Task Highlights_fill_in_when_the_scan_publishes()
    {
        var usage = new FakePresetUsageService { Complete = false };
        var (vm, _) = MakeUsageVm(usage);                              // must pass dispatch: a => a()
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.All(vm.Items, i => Assert.Empty(i.UsedInPresets));

        usage.Map = FakePresetUsageService.MapFor((0, "Lead", new[]
            { FakePresetUsageService.IrLine("V30") }));                // V30 = an occupied item name
        usage.Complete = true;
        usage.RaiseMapUpdated();
        Assert.NotEmpty(vm.Items.First(i => i.Name == "V30").UsedInPresets);
    }

    [Fact]
    public async Task Delete_with_incomplete_map_finishes_the_scan_and_blocks_when_used()
    {
        var usage = new FakePresetUsageService
        {
            Complete = false,
            Map = FakePresetUsageService.MapFor((0, "Lead", new[]
                { FakePresetUsageService.IrLine("V30") })),
        };
        var (vm, _) = MakeUsageVm(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items.First(i => i.Name == "V30");
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, usage.EnsureCompleteCount);                    // guard finished the scan
        Assert.Contains("used in the following presets", vm.ErrorMessage);
    }

    [Fact]
    public async Task Delete_stays_blocked_when_the_scan_cannot_complete()
    {
        var usage = new FakePresetUsageService
        { Complete = false, FailWith = new InvalidOperationException("link died") };
        var (vm, dev) = MakeUsageVm(usage);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items.First(i => !i.IsEmpty);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);                               // refused, with a message
        Assert.DoesNotContain(dev.CommandLog, c => c.StartsWith("dwrite"));  // nothing deleted
    }

    // ---- reorder (Task 4) ----

    /// <summary>Seed-list variant of <see cref="MakeUsageVm"/>: builds a fresh
    /// FakeIrDevice from (name, markerByte) pairs and a default FakePresetUsageService.</summary>
    private (IrListViewModel vm, FakeIrDevice dev, FakePresetUsageService usage) MakeWithUsage(
        (string Name, byte Marker)[] seed)
    {
        var dev = new FakeIrDevice();
        for (int i = 0; i < seed.Length; i++)
            dev.SeedIr(i, seed[i].Name, Enumerable.Repeat(seed[i].Marker, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var usage = new FakePresetUsageService();
        return (new IrListViewModel(svc, writesAllowed: true, usage: usage), dev, usage);
    }

    [Fact] public async Task MoveItemDown_reorders_items_and_touches_usage_never()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, usage.InvalidateCount);
        Assert.Equal(0, usage.MovedCount);
    }

    [Fact] public async Task Reorder_is_allowed_on_a_referenced_ir()
    {
        var (vm, dev, usage) = MakeWithUsage(seed: new[] { ("A", (byte)0xA0), ("B", (byte)0xB0) });
        usage.Map = FakePresetUsageService.MapFor((0, "P0", new[] { FakePresetUsageService.IrLine("A") }));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Null(vm.ErrorMessage);
    }
}
