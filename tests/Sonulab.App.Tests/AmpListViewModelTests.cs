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
}
