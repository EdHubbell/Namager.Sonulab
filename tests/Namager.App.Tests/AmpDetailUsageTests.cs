using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core.Services;
using Sonulab.Distill;
using Xunit;

public class AmpDetailUsageTests
{
    sealed class RecordingNavigator : IPresetNavigator
    {
        public readonly List<(int Index, string Name)> Calls = new();
        public void NavigateToPreset(int index, string name) => Calls.Add((index, name));
    }

    static AmpDetailViewModel NewDetail(FakePresetUsageService usage, IPresetNavigator? nav = null) =>
        new((_, _) => Task.FromResult<AmpMetadata?>(null),
            usage: usage, navigator: nav, dispatch: a => a());

    /// <summary>A map where "Princeton" is the amp of presets 2 and 10.</summary>
    static PresetUsageMap PrincetonUsedBy(params (int Slot, string Name)[] presets) =>
        FakePresetUsageService.MapFor(
            presets.Select(p => (p.Slot, p.Name, new[] { FakePresetUsageService.AmpLine("Princeton") })).ToArray());

    [Fact] public async Task Usage_is_Checking_while_the_scan_is_incomplete()
    {
        var usage = new FakePresetUsageService { Complete = false };
        var vm = NewDetail(usage);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);

        Assert.Equal(AmpUsageState.Checking, vm.UsageState);
        Assert.True(vm.IsUsageChecking);
        Assert.False(vm.IsUsageEmpty);      // must NOT read as "unused" while still scanning
        Assert.Empty(vm.UsedInPresets);
    }

    [Fact] public async Task An_unused_amp_reports_Complete_and_empty()
    {
        var usage = new FakePresetUsageService { Complete = true };
        var vm = NewDetail(usage);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);

        Assert.Equal(AmpUsageState.Complete, vm.UsageState);
        Assert.True(vm.IsUsageEmpty);
        Assert.Empty(vm.UsedInPresets);
    }

    [Fact] public async Task A_used_amp_lists_the_presets_in_slot_order()
    {
        var usage = new FakePresetUsageService
        {
            Complete = true,
            Map = PrincetonUsedBy((2, "Crunch"), (10, "Clean")),
        };
        var vm = NewDetail(usage);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);

        Assert.Equal(AmpUsageState.Complete, vm.UsageState);
        Assert.Equal(new[] { 2, 10 }, vm.UsedInPresets.Select(r => r.Index));
        Assert.False(vm.IsUsageEmpty);
    }

    [Fact] public async Task Usage_refreshes_when_the_scan_publishes_an_update()
    {
        var usage = new FakePresetUsageService { Complete = false };
        var vm = NewDetail(usage);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);
        Assert.Equal(AmpUsageState.Checking, vm.UsageState);

        usage.Map = PrincetonUsedBy((2, "Crunch"));
        usage.Complete = true;
        usage.RaiseMapUpdated();

        Assert.Equal(AmpUsageState.Complete, vm.UsageState);
        Assert.Single(vm.UsedInPresets);
    }

    [Fact] public async Task Clicking_a_preset_navigates_to_its_slot()
    {
        var usage = new FakePresetUsageService
        {
            Complete = true,
            Map = PrincetonUsedBy((2, "Crunch")),
        };
        var nav = new RecordingNavigator();
        var vm = NewDetail(usage, nav);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);

        vm.GoToPresetCommand.Execute(vm.UsedInPresets[0]);

        Assert.Equal((2, "Crunch"), Assert.Single(nav.Calls));
    }

    [Fact] public async Task Clearing_the_pane_drops_the_usage_list()
    {
        var usage = new FakePresetUsageService
        {
            Complete = true,
            Map = PrincetonUsedBy((2, "Crunch")),
        };
        var vm = NewDetail(usage);
        await vm.LoadAsync(0, "Princeton", isEmpty: false);
        Assert.Single(vm.UsedInPresets);

        vm.Clear();

        Assert.Empty(vm.UsedInPresets);
    }
}
