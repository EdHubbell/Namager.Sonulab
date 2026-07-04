using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class AmpListViewModelTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"amp-vm-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }

    private (AmpListViewModel vm, FakeAmpDevice dev) Make(bool writes = true)
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.SeedAmp(1, "Crunch", Enumerable.Repeat((byte)2, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        return (new AmpListViewModel(svc, writes), dev);
    }

    [Fact]
    public async Task Refresh_loads_30_items()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Items.Count);
        Assert.Equal("Clean", vm.Items[0].Name);
        Assert.Equal(1, vm.Items[0].DisplaySlot);
        Assert.True(vm.Items[5].IsEmpty);
    }

    [Fact]
    public async Task Delete_selected_clears_slot_and_reloads()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Null(dev.SlotNames[1]);
        Assert.True(vm.Items[1].IsEmpty);
    }

    [Fact]
    public async Task Delete_is_gated_when_writes_not_allowed()
    {
        var (vm, dev) = Make(writes: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("Clean", dev.SlotNames[0]);            // untouched
    }

    [Fact]
    public async Task CommitRename_renames_and_reloads()
    {
        var (vm, dev) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Cleaner";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("Cleaner", dev.SlotNames[0]);
        Assert.Equal("Cleaner", vm.Items[0].Name);
    }

    [Fact]
    public async Task Service_error_lands_in_ErrorMessage_not_a_crash()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "naïve";                            // non-ASCII -> AmpServiceException
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("ASCII", vm.ErrorMessage);
    }

    // ---- upload flow (Task 6) ----

    private sealed record UploadHarness(AmpListViewModel Vm, FakeAmpDevice Dev, List<string> DistillCalls, string DistilledDir);

    private UploadHarness MakeUpload(
        AmpListViewModel.DistillRunner? distill = null, bool writes = true, int seedCount = 2)
    {
        var dev = new FakeAmpDevice();
        for (int i = 0; i < seedCount; i++)
            dev.SeedAmp(i, $"Amp{i}", Enumerable.Repeat((byte)(i + 1), 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var calls = new List<string>();
        var distilledDir = Path.Combine(Path.GetTempPath(), $"distilled-{Guid.NewGuid():N}");
        AmpListViewModel.DistillRunner runner = distill ?? ((nam, outPath, p, ct) =>
        {
            calls.Add($"{nam}|{outPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, Enumerable.Repeat((byte)0xD1, 12288).ToArray());
            return Task.CompletedTask;
        });
        var vm = new AmpListViewModel(svc, writes, runner, distilledDir, dispatch: a => a());
        return new UploadHarness(vm, dev, calls, distilledDir);
    }

    private static string TempFile(string name)
    {
        var p = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(p, Enumerable.Repeat((byte)0xEE, 12288).ToArray());
        return p;
    }

    [Fact]
    public async Task BeginUpload_prefills_name_and_empty_slots()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        var nam = TempFile("My Very Long Amp Model Name Overflowing.nam");
        h.Vm.BeginUploadCommand.Execute(nam);
        Assert.True(h.Vm.IsUploadPanelOpen);
        Assert.Equal(31, h.Vm.UploadName.Length);           // stem truncated to the device cap
        Assert.Equal(28, h.Vm.EmptySlots.Count);            // 30 - 2 seeded
        Assert.Equal(2, h.Vm.SelectedEmptySlot);            // first empty index
    }

    [Fact]
    public async Task BeginUpload_with_no_empty_slots_blocks_with_message()
    {
        var h = MakeUpload(seedCount: 30);
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("x.nam"));
        Assert.False(h.Vm.IsUploadPanelOpen);
        Assert.NotNull(h.Vm.UploadBlockedMessage);
    }

    [Fact]
    public async Task StartUpload_nam_distills_to_library_then_uploads_and_selects()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Plexi.nam"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Single(h.DistillCalls);
        Assert.EndsWith(Path.Combine(h.DistilledDir, "Plexi.vxamp"), h.DistillCalls[0].Split('|')[1]);
        Assert.Equal("Plexi", h.Dev.SlotNames[2]);          // first empty slot
        Assert.Equal(0xD1, h.Dev.SlotBlobs[2]![0]);         // distilled bytes, not source bytes
        Assert.True(h.Vm.IsUploadPanelOpen);                // stays open to show the Done state
        Assert.StartsWith("Done", h.Vm.UploadStatus);
        Assert.Equal(2, h.Vm.Selected?.Index);              // new amp selected
        Assert.Null(h.Vm.UploadError);
    }

    [Fact]
    public async Task StartUpload_vxamp_skips_distillation()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Backup.vxamp"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Empty(h.DistillCalls);
        Assert.Equal("Backup", h.Dev.SlotNames[2]);
        Assert.Equal(0xEE, h.Dev.SlotBlobs[2]![0]);         // the file's own bytes
    }

    [Fact]
    public async Task StartUpload_rejects_duplicate_name()
    {
        var h = MakeUpload();
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Amp0.nam"));   // collides with seeded slot 0
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.NotNull(h.Vm.UploadError);
        Assert.Empty(h.DistillCalls);                        // failed before any work
        Assert.Null(h.Dev.SlotNames[2]);
    }

    [Fact]
    public async Task Distill_failure_surfaces_in_UploadError_and_device_is_untouched()
    {
        var h = MakeUpload(distill: (n, o, p, ct) =>
            throw new Sonulab.Distill.DistillException("numeric explosion"));
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Bad.nam"));
        await h.Vm.StartUploadCommand.ExecuteAsync(null);
        Assert.Contains("numeric explosion", h.Vm.UploadError);
        Assert.True(h.Vm.IsUploadPanelOpen);                 // stays open to show the error
        Assert.Null(h.Dev.SlotNames[2]);
        Assert.DoesNotContain(h.Dev.CommandLog, c => c.StartsWith("dwrite"));
    }

    [Fact]
    public async Task Cancel_during_distill_cancels_cleanly()
    {
        var tcs = new TaskCompletionSource();
        var h = MakeUpload(distill: async (n, o, p, ct) =>
        {
            tcs.SetResult();                                 // signal: distill has started
            await Task.Delay(Timeout.Infinite, ct);          // parks until cancelled
        });
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Slow.nam"));
        var run = h.Vm.StartUploadCommand.ExecuteAsync(null);
        await tcs.Task;
        Assert.True(h.Vm.CanCancelUpload);                   // cancellable while distilling
        h.Vm.CancelUploadCommand.Execute(null);
        await run;
        Assert.Contains("ancel", h.Vm.UploadError);          // "Cancelled."
        Assert.False(h.Vm.IsUploading);
        Assert.Null(h.Dev.SlotNames[2]);
    }

    [Fact]
    public async Task List_ops_are_gated_while_uploading()
    {
        var tcs = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var h = MakeUpload(distill: async (n, o, p, ct) =>
        {
            tcs.SetResult();
            await release.Task;
            Directory.CreateDirectory(Path.GetDirectoryName(o)!);
            File.WriteAllBytes(o, Enumerable.Repeat((byte)0xD1, 12288).ToArray());
        });
        await h.Vm.RefreshCommand.ExecuteAsync(null);
        h.Vm.BeginUploadCommand.Execute(TempFile("Slow2.nam"));
        var run = h.Vm.StartUploadCommand.ExecuteAsync(null);
        await tcs.Task;
        Assert.False(h.Vm.CanMutate);
        Assert.False(h.Vm.CanRefresh);
        h.Vm.Selected = h.Vm.Items[0];
        await h.Vm.DeleteCommand.ExecuteAsync(null);          // must no-op
        Assert.Equal("Amp0", h.Dev.SlotNames[0]);
        release.SetResult();
        await run;
        Assert.True(h.Vm.CanMutate);
    }
}
