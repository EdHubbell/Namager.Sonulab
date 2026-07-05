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

    [Fact] public async Task Writes_are_gated_when_not_allowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal("A", vm.Items[0].Name);   // unchanged — writes were gated
        Assert.Equal("B", vm.Items[1].Name);
    }

    [Fact] public async Task Move_flags_reflect_position_and_occupancy()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.Items[0].CanMoveUp);     // first slot
        Assert.True(vm.Items[0].CanMoveDown);
        Assert.True(vm.Items[2].CanMoveUp);
        Assert.True(vm.Items[2].CanMoveDown);    // slot 2 < 29, gap below
        Assert.False(vm.Items[5].CanMoveUp);     // empty slot — no buttons
        Assert.False(vm.Items[5].CanMoveDown);
        Assert.False(vm.Items[29].CanMoveDown);  // last slot
    }

    [Fact] public async Task MoveItemDown_moves_that_row_and_selects_it()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);   // A (slot 0) down, swaps with B
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal("A", vm.Selected?.Name);                    // selection follows the moved preset
    }

    [Fact] public async Task MoveItemUp_moves_that_row_independent_of_selection()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];                               // selection is on A, not the moved row
        await vm.MoveItemUpCommand.ExecuteAsync(vm.Items[2]);     // C (slot 2) up, swaps with B
        Assert.Equal("A", vm.Items[0].Name);
        Assert.Equal("C", vm.Items[1].Name);
        Assert.Equal("B", vm.Items[2].Name);
    }

    [Fact] public async Task MoveItemDown_into_empty_gap_relocates()
    {
        var (vm, _) = Make();                                    // A,B,C at 0..2; slot 3 empty
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[2]);   // C down into empty slot 3
        Assert.True(vm.Items[2].IsEmpty);
        Assert.Equal("C", vm.Items[3].Name);
    }

    [Fact] public async Task MoveItem_on_empty_row_is_noop()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemUpCommand.ExecuteAsync(vm.Items[5]);     // empty slot
        Assert.True(vm.Items[5].IsEmpty);
        Assert.Equal("A", vm.Items[0].Name);                     // nothing moved
    }

    [Fact] public async Task MoveItem_is_gated_when_writes_disallowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.MoveItemDownCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("A", vm.Items[0].Name);                     // unchanged — writes gated
        Assert.Equal("B", vm.Items[1].Name);
    }

    [Fact] public async Task InPlace_rename_changes_name()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];                       // "A"
        item.BeginRenameCommand.Execute(null);
        Assert.True(item.IsEditing);
        Assert.Equal("A", item.EditName);
        item.EditName = "Aprime";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("Aprime", vm.Items[0].Name);
    }

    [Fact] public async Task CommitRename_noop_when_blank_and_exits_edit()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "   ";                        // whitespace -> treated as no change
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("A", vm.Items[0].Name);
        Assert.False(item.IsEditing);
    }

    [Fact] public async Task BeginRename_on_empty_row_does_nothing()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Items[5].BeginRenameCommand.Execute(null); // empty slot
        Assert.False(vm.Items[5].IsEditing);
    }

    [Fact] public async Task CancelRename_exits_edit_without_change()
    {
        var (vm, _) = Make();
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Aprime";
        item.CancelRenameCommand.Execute(null);
        await vm.CommitRenameCommand.ExecuteAsync(item);   // guarded by IsEditing -> no-op
        Assert.False(item.IsEditing);
        Assert.Equal("A", vm.Items[0].Name);
    }

    [Fact] public async Task InPlace_rename_gated_when_writes_disallowed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        var item = vm.Items[0];
        item.BeginRenameCommand.Execute(null);
        item.EditName = "Nope";
        await vm.CommitRenameCommand.ExecuteAsync(item);
        Assert.Equal("A", vm.Items[0].Name);          // unchanged (gated)
        Assert.False(item.IsEditing);                 // left edit mode
    }

    // Counts content reads so we can prove the toolbar move is content-free (perf spec §5).
    sealed class DreadCountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public DreadCountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public System.Threading.Tasks.Task OpenAsync(System.Threading.CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public System.Threading.Tasks.Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Toolbar_move_reads_no_preset_content()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await dev.OpenAsync();
        var link = new DreadCountingLink(dev);
        var repo = new DeviceRepository(new SonuClient(link));
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.MoveDownCommand.ExecuteAsync(null);           // toolbar path
        Assert.Equal("B", vm.Items[0].Name);
        Assert.Equal("A", vm.Items[1].Name);
        Assert.Equal(0, link.Dreads);                          // lean: zero content reads
    }
}
