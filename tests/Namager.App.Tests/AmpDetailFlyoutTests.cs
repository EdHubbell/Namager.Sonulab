using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Sonulab.Distill;
using Xunit;

/// <summary>#9: the preset editor's amp field opens the shared amp-detail card in a flyout.</summary>
public class AmpDetailFlyoutTests
{
    /// <summary>Editor VM whose `root\amp` list read returns <paramref name="ampNames"/> (slot index
    /// == position, so pass "" for empty slots), with an injectable metadata reader.</summary>
    static ParameterEditorViewModel NewEditorWithAmps(
        string[] ampNames,
        Func<int, CancellationToken, Task<AmpMetadata?>>? readMetadata = null)
    {
        var dev = new FakeSonuLink();
        dev.SeedBrowse(@"root\app",
            // type:"plist" is what the device actually reports for the amp picker (see the other
            // editor tests); type:"item" is not in EditableTypes and would be skipped entirely.
            "root\\app\\amp\\amp:{\"desc\":\"Amp\",\"value\":\"\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}");
        dev.SeedList(@"root\amp", ampNames);
        dev.OpenAsync().GetAwaiter().GetResult();
        return new ParameterEditorViewModel(new SonuClient(dev),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(Array.Empty<string>()),
            readAmpMetadata: readMetadata ?? ((_, _) => Task.FromResult<AmpMetadata?>(null)));
    }

    static async Task<ParameterFieldViewModel> AmpRefFieldAsync(ParameterEditorViewModel vm, string name)
    {
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var f = vm.Blocks.SelectMany(b => b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields)))
                         .Single(x => x.RefSource == @"root\amp");
        f.Text = name;
        return f;
    }

    [Fact] public async Task Opening_the_flyout_loads_the_amp_named_by_the_field()
    {
        var vm = NewEditorWithAmps(new[] { "Tweed", "Princeton", "Plexi" });
        var field = await AmpRefFieldAsync(vm, "Princeton");

        await vm.ShowAmpDetailCommand.ExecuteAsync(field);

        Assert.Equal("Princeton", vm.AmpDetail.Name);
        Assert.Equal(2, vm.AmpDetail.DisplaySlot);      // 1-based display of slot index 1
        Assert.True(vm.AmpDetail.IsVisible);
        Assert.Null(vm.AmpDetail.Error);
    }

    [Fact] public async Task An_unresolvable_amp_name_surfaces_an_error_instead_of_throwing()
    {
        var vm = NewEditorWithAmps(new[] { "Tweed" });
        var field = await AmpRefFieldAsync(vm, "Ghost");     // not in the catalog

        await vm.ShowAmpDetailCommand.ExecuteAsync(field);

        Assert.NotNull(vm.AmpDetail.Error);
        Assert.Empty(vm.AmpDetail.Fields);
    }

    [Fact] public async Task A_failing_metadata_read_surfaces_an_error_and_does_not_escape()
    {
        var vm = NewEditorWithAmps(new[] { "Tweed" },
            readMetadata: (_, _) => throw new InvalidOperationException("link died"));
        var field = await AmpRefFieldAsync(vm, "Tweed");

        await vm.ShowAmpDetailCommand.ExecuteAsync(field);   // must not throw

        Assert.NotNull(vm.AmpDetail.Error);
    }

    [Fact] public async Task Empty_slots_do_not_shift_the_resolved_slot_number()
    {
        // The picker filters empty slots out of its options, so an option's position is NOT its slot.
        // Resolution must go through the raw list, which keeps the gaps.
        var vm = NewEditorWithAmps(new[] { "", "", "Princeton" });
        var field = await AmpRefFieldAsync(vm, "Princeton");

        await vm.ShowAmpDetailCommand.ExecuteAsync(field);

        Assert.Equal(3, vm.AmpDetail.DisplaySlot);
    }

    [Fact] public async Task An_empty_field_value_opens_nothing()
    {
        var vm = NewEditorWithAmps(new[] { "Tweed" });
        var field = await AmpRefFieldAsync(vm, "");

        await vm.ShowAmpDetailCommand.ExecuteAsync(field);

        Assert.False(vm.AmpDetail.IsVisible);
    }
}
