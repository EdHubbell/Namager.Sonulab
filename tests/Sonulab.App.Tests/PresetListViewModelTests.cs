using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetListViewModelTests
{
    static (PresetListViewModel vm, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        return (new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true), dev);
    }

    [Fact] public async Task Refresh_loads_30_items_with_display_slots()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Items.Count);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.Equal(1, vm.Items[0].DisplaySlot);
        Assert.True(vm.Items[5].IsEmpty);
    }

    [Fact] public async Task MoveDown_moves_selected_and_reloads()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                       // "A" at slot 0
        await vm.MoveDownCommand.ExecuteAsync(null);     // -> slot 1
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
    }

    [Fact] public async Task Duplicate_copies_selected_into_first_empty_slot()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[1];                       // "B"
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Contains(vm.Items, i => i.Name == "B copy");
    }

    [Fact] public async Task Delete_empties_selected_slot()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[2];                       // "C"
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.True(vm.Items[2].IsEmpty);
    }

    [Fact] public async Task Rename_changes_selected_name()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.RenameCommand.ExecuteAsync("Aprime");
        Assert.Equal("Aprime", vm.Items[0].Name);
    }
}
