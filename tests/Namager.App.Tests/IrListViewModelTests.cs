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
}
