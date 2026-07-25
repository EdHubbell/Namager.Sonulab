using Namager.App.Services;
using Namager.App.ViewModels;
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.Equal(new[] { "amp", "delay" }, vm.Blocks.Select(b => b.Header.ToLowerInvariant()).ToArray());
    }

    [Fact] public async Task Hidden_param_is_excluded_but_new_param_appears()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var delay = vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase));
        var sub = Assert.Single(delay.SubGroups);
        Assert.Contains(sub.Fields, f => f.Path.EndsWith(@"\tape"));
    }

    [Fact] public async Task Output_block_is_skipped()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.DoesNotContain(vm.Blocks, b => b.Header.Equals("output", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Save_writes_only_dirty_fields_across_blocks()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d);
        vm.PresetName = "P";
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var gain = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\gain"));
        gain.Number = -6.0;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("-6", await new SonuClient(d).ReadValueAsync(@"root\app\amp\gain"));
    }

    [Fact] public async Task Save_reports_saved_to_status()
    {
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"root\app\amp\sag" }),
            status);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P0"));
        vm.PresetName = "P1";                     // makes SaveAsync issue the device save
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Saved", status.Succeeded);
        Assert.Empty(status.Failed);
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "Quad Reverb"));
        Assert.Equal("Quad Reverb", vm.PresetName);
        Assert.NotEmpty(vm.Blocks);
        Assert.Equal(1, link.PresetWrites);                 // activated on device
        Assert.False(vm.IsLoading);
        Assert.Equal(new[] { true, false }, states);        // disabled during load, re-enabled after
    }

    [Fact] public async Task LoadFor_dedups_same_preset_name()
    {
        var (vm, link) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(2, "X"));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(2, "X"));           // same name -> no-op
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Clean", "Lead", "Rhythm" }, field.Options);   // non-empty names only
        Assert.Equal("plist", field.Kind);
    }

    [Fact] public async Task Ref_field_with_deleted_current_value_still_shows_it()
    {
        var vm = VmFor(RefDev("Clean", "Rhythm"));                   // "Lead" not on the device
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Equal(new[] { "Lead", "Clean", "Rhythm" }, field.Options);
    }

    [Fact] public async Task Missing_ref_list_degrades_without_failing_the_load()
    {
        var vm = VmFor(RefDev());                                    // no root\amp seeded at all
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));   // must not throw
        var field = vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path.EndsWith(@"\amp"));
        Assert.Empty(field.Options);                                 // renders as today
    }

    // ---- collapsed-by-default + per-session expansion state (editor-polish Task 3) ----

    [Fact] public async Task Blocks_start_collapsed()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P1"));
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P1"));
        vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded = true;

        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "P2"));          // rebuilds all sections
        Assert.True(vm.Blocks.First(b => b.Header.Equals("amp", StringComparison.OrdinalIgnoreCase)).IsExpanded);
        Assert.False(vm.Blocks.First(b => b.Header.Equals("delay", StringComparison.OrdinalIgnoreCase)).IsExpanded);
    }

    [Fact] public async Task Collapsing_again_is_also_remembered()
    {
        var (vm, _) = LoadForVm();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P1"));
        var block = vm.Blocks[0];
        block.IsExpanded = true;
        block.IsExpanded = false;
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "P2"));
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.Equal(0, link.ListReads);          // fails before the fix (prefetch included "item")
    }

    // ---- slot tracking + usage-map refresh on save (v0.9.7 Task 3) ----

    static ParameterEditorViewModel VmWithUsage(FakeSonuLink d, FakePresetUsageService usage) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"root\app\amp\sag" }),
            usage: usage);

    [Fact] public async Task LoadFor_records_the_slot_index()
    {
        var d = Dev(); await d.OpenAsync();
        var usage = new FakePresetUsageService();
        var vm = VmWithUsage(d, usage);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(4, "Clean"));
        Assert.Equal(4, vm.LoadedIndex);
        Assert.Equal("Clean", vm.PresetName);
    }

    [Fact] public async Task LoadFor_updates_the_slot_index_even_when_the_preset_is_already_loaded()
    {
        // A reorder moves the SAME preset to a new slot: the content load is correctly skipped,
        // but the recorded index must follow, or a later save patches the wrong slot.
        var d = Dev(); await d.OpenAsync();
        var vm = VmWithUsage(d, new FakePresetUsageService());
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(4, "Clean"));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(5, "Clean"));
        Assert.Equal(5, vm.LoadedIndex);
    }

    [Fact] public async Task LoadFor_failure_rolls_back_LoadedIndex_to_the_previously_loaded_slot()
    {
        // Fix round 1 finding: LoadedIndex was assigned unconditionally before the device write,
        // and (correctly) left unassigned-back on failure — so PresetName/_loadedName kept
        // describing the OLD preset while LoadedIndex pointed at the slot that failed to load.
        // A later download/usage-notify keyed off LoadedIndex would then target the wrong slot
        // under the wrong name. A failed load must leave the editor exactly as it was.
        var d = Dev(); await d.OpenAsync();
        var vm = VmWithUsage(d, new FakePresetUsageService());
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(4, "Clean"));
        Assert.Equal(4, vm.LoadedIndex);
        Assert.Equal("Clean", vm.PresetName);

        d.Close();                                          // next device call now throws ("link not open")
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(7, "Other"));

        Assert.Equal(4, vm.LoadedIndex);                     // rolled back, not left at the failed slot
        Assert.Equal("Clean", vm.PresetName);                // still describes the previously loaded preset
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact] public async Task Save_notifies_the_usage_service_for_the_loaded_slot()
    {
        var d = Dev(); await d.OpenAsync();
        var usage = new FakePresetUsageService();
        var vm = VmWithUsage(d, usage);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(4, "Clean"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, usage.ContentChangedCount);
        Assert.Equal((4, "Clean"), usage.LastContentChanged);
    }

    [Fact] public async Task Save_without_a_loaded_preset_does_not_notify()
    {
        var d = Dev(); await d.OpenAsync();
        var usage = new FakePresetUsageService();
        var vm = VmWithUsage(d, usage);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, usage.ContentChangedCount);
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
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P1"));
        var amp = vm.Blocks.First(b => b.Header == "Same" && b.Fields.Any(f => f.Path.EndsWith(@"\gain")));
        var delay = vm.Blocks.First(b => b.Header == "Same" && b.Fields.Any(f => f.Path.EndsWith(@"\fdbk")));
        amp.IsExpanded = true;
        // Distinct name from the load above: LoadForCommand no-ops when the requested name equals
        // the one already loaded, and this second call must force an actual rebuild to test anything.
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P2"));   // rebuild: reapplies expansion state keyed by block path
        var ampAfter = vm.Blocks.First(b => b.Fields.Any(f => f.Path.EndsWith(@"\gain")));
        var delayAfter = vm.Blocks.First(b => b.Fields.Any(f => f.Path.EndsWith(@"\fdbk")));
        Assert.True(ampAfter.IsExpanded);
        Assert.False(delayAfter.IsExpanded);       // would also be true if state were header-keyed
    }

    // ---- refreshable picker options (v0.9.7 Task 5) ----

    // Device with an amp-ref field whose options come from `read root\amp`.
    // Named distinctly from the existing RefDev(params string[]) helper above to avoid an
    // ambiguous-call conflict (RefDev() would match both a parameterless and a params overload).
    static FakeSonuLink RefreshDev()
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\amp:{\"desc\":\"Model\",\"value\":\"mA\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}");
        d.SeedList(@"root\amp", new[] { "mA", "mB" });
        return d;
    }

    [Fact] public async Task RefreshRefOptions_is_a_noop_while_the_catalog_has_not_moved()
    {
        var d = RefreshDev(); await d.OpenAsync();
        var catalog = new CatalogVersion();
        var vm = new ParameterEditorViewModel(new SonuClient(d), catalog: catalog);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        d.SeedList(@"root\amp", new[] { "mA", "mB", "mC" });
        await vm.RefreshRefOptionsAsync();
        var field = vm.Blocks.SelectMany(b => b.Fields).Single(f => f.Path.EndsWith(@"\amp\amp"));
        Assert.Equal(new[] { "mA", "mB" }, field.Options);
    }

    [Fact] public async Task RefreshRefOptions_rereads_the_list_after_a_catalog_bump()
    {
        var d = RefreshDev(); await d.OpenAsync();
        var catalog = new CatalogVersion();
        var vm = new ParameterEditorViewModel(new SonuClient(d), catalog: catalog);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        d.SeedList(@"root\amp", new[] { "mA", "mC" });        // mB deleted, mC uploaded
        catalog.Bump();
        await vm.RefreshRefOptionsAsync();
        var field = vm.Blocks.SelectMany(b => b.Fields).Single(f => f.Path.EndsWith(@"\amp\amp"));
        Assert.Equal(new[] { "mA", "mC" }, field.Options);
    }

    [Fact] public async Task RefreshRefOptions_keeps_the_current_value_when_the_device_drops_it()
    {
        var d = RefreshDev(); await d.OpenAsync();
        var catalog = new CatalogVersion();
        var vm = new ParameterEditorViewModel(new SonuClient(d), catalog: catalog);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        d.SeedList(@"root\amp", new[] { "mB" });              // the loaded preset's own amp is gone
        catalog.Bump();
        await vm.RefreshRefOptionsAsync();
        var field = vm.Blocks.SelectMany(b => b.Fields).Single(f => f.Path.EndsWith(@"\amp\amp"));
        Assert.Equal("mA", field.Text);
        Assert.Contains("mA", field.Options);
    }
}
