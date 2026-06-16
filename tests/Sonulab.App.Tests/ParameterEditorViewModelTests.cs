using Sonulab.App.Services;
using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class ParameterEditorViewModelTests
{
    static FakeSonuLink Dev()
    {
        var d = new FakeSonuLink();
        d.SeedScalar(@"root\app\amp\on_off", "\"ON\"");
        d.SeedBrowse(@"root\app",
            // amp block: a hidden leaf (sag), a visible float (gain), an enum (on_off)
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"unit\":\"dB\"}",
            "root\\app\\amp\\sag:{\"desc\":\"Sag\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":1.0}",
            // delay block with a folder (tcfolder) holding a leaf, plus a brand-new unmapped leaf
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\tcfolder:{\"desc\":\"Tone and Character\",\"value\":\"\",\"type\":\"item\",\"item_type\":\"vfolder\"}",
            "root\\app\\delay\\tcfolder\\tape:{\"desc\":\"Tape\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}",
            "root\\app\\delay\\newknob:{\"desc\":\"New Knob\",\"value\":1.0,\"type\":\"float\",\"min\":0.0,\"max\":10.0}",
            // output block must be skipped (out of scope)
            "root\\app\\output\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}");
        return d;
    }

    static ParameterEditorViewModel Vm(FakeSonuLink d) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"root\app\amp\sag" }));

    [Fact] public async Task Load_groups_into_blocks_in_order()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "amp", "delay" }, vm.Blocks.Select(b => b.Header.ToLowerInvariant()).ToArray());
    }

    [Fact] public async Task Hidden_param_is_excluded_but_new_param_appears()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var amp = vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(amp.Fields, f => f.Path.EndsWith(@"\sag"));     // blocklisted
        Assert.Contains(amp.Fields, f => f.Path.EndsWith(@"\gain"));
        var delay = vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(delay.Fields, f => f.Path.EndsWith(@"\newknob"));     // new/unmapped still shown
    }

    [Fact] public async Task Folder_nodes_become_subgroups()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var delay = vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase));
        var sub = Assert.Single(delay.SubGroups);
        Assert.Contains(sub.Fields, f => f.Path.EndsWith(@"\tape"));
    }

    [Fact] public async Task Output_block_is_skipped()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Blocks, b => b.Header.Equals("output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Save_writes_only_dirty_fields_across_blocks()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        vm.PresetName = "P";
        await vm.LoadCommand.ExecuteAsync(null);
        var gain = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\gain"));
        gain.Number = -6.0;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("-6", await new SonuClient(d).ReadValueAsync(@"root\app\amp\gain"));
    }
}
