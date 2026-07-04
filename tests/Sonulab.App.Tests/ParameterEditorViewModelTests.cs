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

    static ParameterEditorViewModel VmFor(FakeSonuLink d) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(System.Array.Empty<string>()));

    [Fact] public async Task Block_Enabled_reflects_on_off_leaf()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}",
            "root\\app\\gate\\on_off:{\"desc\":\"Enable\",\"value\":\"OFF\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\gate\\threshold:{\"desc\":\"Threshold\",\"value\":-60.0,\"type\":\"float\",\"min\":-100.0,\"max\":-20.0}",
            "root\\app\\eq\\low:{\"desc\":\"Low\",\"value\":0.0,\"type\":\"float\",\"min\":-15.0,\"max\":15.0}");
        await d.OpenAsync();
        var vm = VmFor(d);
        await vm.LoadCommand.ExecuteAsync(null);
        bool? En(string h) => vm.Blocks.First(b => b.Header.Equals(h, StringComparison.OrdinalIgnoreCase)).Enabled;
        Assert.True(En("amp"));
        Assert.False(En("gate"));
        Assert.Null(En("eq"));        // eq has no on_off -> no indicator
    }

    [Fact] public async Task Block_Enabled_updates_when_on_off_field_changes()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        await d.OpenAsync();
        var vm = VmFor(d);
        await vm.LoadCommand.ExecuteAsync(null);
        var amp = vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase));
        Assert.True(amp.Enabled);
        bool raised = false;
        amp.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BlockSectionViewModel.Enabled)) raised = true; };
        amp.EnableField!.Text = "OFF";
        Assert.False(amp.Enabled);
        Assert.True(raised);
    }

    // Counts device commands by wrapping the link (FakeSonuLink.SendAsync is not virtual).
    sealed class CountingLink : ISonuLink
    {
        private readonly ISonuLink _inner;
        public int Browses, PresetWrites;
        public CountingLink(ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public System.Threading.Tasks.Task OpenAsync(System.Threading.CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public System.Threading.Tasks.Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.StartsWith("browse ", StringComparison.Ordinal)) Browses++;
            else if (command.StartsWith("write root\\app\\preset:", StringComparison.Ordinal)) PresetWrites++;
            return _inner.SendAsync(command, ct);
        }
    }

    static (ParameterEditorViewModel vm, CountingLink link) LoadForVm()
    {
        var dev = new FakeSonuLink();
        dev.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        var vm = new ParameterEditorViewModel(new SonuClient(link),
            new LabelService(new Dictionary<string, string>()), new ParameterExposure(System.Array.Empty<string>()));
        return (vm, link);
    }

    [Fact] public async Task LoadFor_activates_preset_builds_blocks_and_toggles_IsLoading()
    {
        var (vm, link) = LoadForVm();
        var states = new System.Collections.Generic.List<bool>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ParameterEditorViewModel.IsLoading)) states.Add(vm.IsLoading); };
        await vm.LoadForCommand.ExecuteAsync("Quad Reverb");
        Assert.Equal("Quad Reverb", vm.PresetName);
        Assert.NotEmpty(vm.Blocks);
        Assert.Equal(1, link.PresetWrites);                 // activated on device
        Assert.False(vm.IsLoading);
        Assert.Equal(new[] { true, false }, states);        // disabled during load, re-enabled after
    }

    [Fact] public async Task LoadFor_dedups_same_preset_name()
    {
        var (vm, link) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("X");
        await vm.LoadForCommand.ExecuteAsync("X");           // same name -> no-op
        Assert.Equal(1, link.PresetWrites);
        Assert.Equal(1, link.Browses);
    }

    // ---- ref-populated dropdowns (editor-polish Task 2) ----

    static FakeSonuLink RefDev(params string[] ampNames)
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\amp:{\"desc\":\"Amp model\",\"value\":\"Lead\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}");
        if (ampNames.Length > 0) d.SeedList(@"root\amp", ampNames);
        d.OpenAsync().GetAwaiter().GetResult();
        return d;
    }

    [Fact] public async Task Ref_field_gets_options_from_device_list()
    {
        var vm = VmFor(RefDev("Clean", "Lead", "", "", "Rhythm"));   // empties are slot padding
        await vm.LoadCommand.ExecuteAsync(null);
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Clean", "Lead", "Rhythm" }, field.Options);   // non-empty names only
        Assert.Equal("plist", field.Kind);
    }

    [Fact] public async Task Ref_field_with_deleted_current_value_still_shows_it()
    {
        var vm = VmFor(RefDev("Clean", "Rhythm"));                   // "Lead" not on the device
        await vm.LoadCommand.ExecuteAsync(null);
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Lead", "Clean", "Rhythm" }, field.Options);
    }

    [Fact] public async Task Missing_ref_list_degrades_without_failing_the_load()
    {
        var vm = VmFor(RefDev());                                    // no root\amp seeded at all
        await vm.LoadCommand.ExecuteAsync(null);                     // must not throw
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Empty(field.Options);                                 // renders as today
    }

    // ---- collapsed-by-default + per-session expansion state (editor-polish Task 3) ----

    [Fact] public async Task Blocks_start_collapsed()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("P1");
        Assert.All(vm.Blocks, b => Assert.False(b.IsExpanded));
    }

    [Fact] public async Task Expansion_survives_preset_switch_per_block()
    {
        var dev = new FakeSonuLink();
        dev.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}",
            "root\\app\\delay\\fdbk:{\"desc\":\"Feedback\",\"value\":30.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0}");
        await dev.OpenAsync();
        var vm = VmFor(dev);
        await vm.LoadForCommand.ExecuteAsync("P1");
        vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded = true;

        await vm.LoadForCommand.ExecuteAsync("P2");          // rebuilds all sections
        Assert.True(vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded);
        Assert.False(vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase)).IsExpanded);
    }

    [Fact] public async Task Collapsing_again_is_also_remembered()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync("P1");
        var block = vm.Blocks[0];
        block.IsExpanded = true;
        block.IsExpanded = false;
        await vm.LoadForCommand.ExecuteAsync("P2");
        Assert.False(vm.Blocks[0].IsExpanded);
    }

    // ---- ride-alongs (ir-tab Task 9) ----

    // Counts list reads so we can prove an item-typed ref costs NO device round-trip.
    sealed class ListReadCountingLink(Sonulab.Core.Transport.ISonuLink inner) : Sonulab.Core.Transport.ISonuLink
    {
        public int ListReads;
        public bool IsOpen => inner.IsOpen;
        public System.Threading.Tasks.Task OpenAsync(System.Threading.CancellationToken ct = default) => inner.OpenAsync(ct);
        public void Close() => inner.Close();
        public System.Threading.Tasks.Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command == @"read root\amp") ListReads++;
            return inner.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Item_typed_ref_is_not_prefetched()
    {
        // Prefetch filter must agree with the field-build loop (which excludes "item"):
        // an item-typed ref must trigger NO read of its ref list.
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0}",
            "root\\app\\delay\\folder:{\"desc\":\"F\",\"value\":\"\",\"type\":\"item\",\"ref\":\"root\\\\amp\"}");
        d.SeedList(@"root\amp", new[] { "ShouldNotBeRead" });
        await d.OpenAsync();
        var link = new ListReadCountingLink(d);
        var vm = new ParameterEditorViewModel(new SonuClient(link),
            new LabelService(new Dictionary<string, string>()), new ParameterExposure(System.Array.Empty<string>()));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(0, link.ListReads);          // fails before the fix (prefetch included "item")
    }

    [Fact] public async Task Expansion_state_keyed_by_block_path_survives_header_relabel()
    {
        var d = Dev(); await d.OpenAsync();
        // amp and delay are mapped to the SAME header text. Under header-keying, both blocks
        // would share one dictionary entry and expanding one would (wrongly) expand both on
        // reload; path-keying keeps them independent.
        var labels = new LabelService(new Dictionary<string, string>
        {
            [@"root\app\amp"] = "Same",
            [@"root\app\delay"] = "Same",
        });
        var vm = new ParameterEditorViewModel(new SonuClient(d), labels, new ParameterExposure(System.Array.Empty<string>()));
        await vm.LoadCommand.ExecuteAsync(null);
        var amp = vm.Blocks.First(b => b.Header == "Same" && b.Fields.Any(f => f.Path.EndsWith(@"\gain")));
        var delay = vm.Blocks.First(b => b.Header == "Same" && b.Fields.Any(f => f.Path.EndsWith(@"\fdbk")));
        amp.IsExpanded = true;
        await vm.LoadCommand.ExecuteAsync(null);   // rebuild: reapplies expansion state keyed by block path
        var ampAfter = vm.Blocks.First(b => b.Fields.Any(f => f.Path.EndsWith(@"\gain")));
        var delayAfter = vm.Blocks.First(b => b.Fields.Any(f => f.Path.EndsWith(@"\fdbk")));
        Assert.True(ampAfter.IsExpanded);
        Assert.False(delayAfter.IsExpanded);       // would also be true if state were header-keyed
    }
}
