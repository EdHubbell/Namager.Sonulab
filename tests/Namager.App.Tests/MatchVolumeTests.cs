using Namager.App.Services;
using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

public class MatchVolumeTests
{
    // Two presets whose only difference is eq\level: the loaded one is flat, slot 1 is +6 dB.
    // Matching the loaded preset TO slot 1 must therefore propose +6 dB.
    static FakeSonuLink Dev()
    {
        var d = new FakeSonuLink();
        d.SeedList(@"root\presets", Names("Loaded", "Louder"));
        d.SeedList(@"root\amp", Names("TestAmp"));
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\amp:{\"desc\":\"Amp\",\"value\":\"TestAmp\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\amp\\vol:{\"desc\":\"Volume\",\"value\":50.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0}",
            "root\\app\\eq\\level:{\"desc\":\"Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\output\\pst\\level:{\"desc\":\"Preset Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0,\"unit\":\"dB\",\"dec\":1}");
        return d;
    }

    // Same fixture as Dev(), except the amp\vol schema VALUE starts at 0 % instead of 50 % — so
    // the loaded preset's live field is silent from the moment it loads, with no manual edit
    // (and therefore no incidental IsDirty) needed to set up the both-sides-silent NaN case.
    static FakeSonuLink DevWithVolAtZero()
    {
        var d = new FakeSonuLink();
        d.SeedList(@"root\presets", Names("Loaded", "Louder"));
        d.SeedList(@"root\amp", Names("TestAmp"));
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\on_off:{\"desc\":\"Enable\",\"value\":\"ON\",\"type\":\"enum\",\"options\":[\"ON\",\"OFF\"]}",
            "root\\app\\amp\\amp:{\"desc\":\"Amp\",\"value\":\"TestAmp\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}",
            "root\\app\\amp\\gain:{\"desc\":\"Gain\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\amp\\vol:{\"desc\":\"Volume\",\"value\":0.0,\"type\":\"float\",\"min\":0.0,\"max\":100.0,\"def\":50.0}",
            "root\\app\\eq\\level:{\"desc\":\"Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0}",
            "root\\app\\output\\pst\\level:{\"desc\":\"Preset Level\",\"value\":0.0,\"type\":\"float\",\"min\":-20.0,\"max\":20.0,\"def\":0.0,\"unit\":\"dB\",\"dec\":1}");
        return d;
    }

    static string[] Names(params string[] used)
    {
        var n = new string[30];
        for (int i = 0; i < n.Length; i++) n[i] = i < used.Length ? used[i] : "";
        return n;
    }

    // The target preset, in the on-disk .pst form DeviceRepository.ReadPresetAsync returns.
    static Sonulab.Core.Model.PresetDocument TargetPst(
        double eqLevel, double pstLevel = 0.0, double ampVol = 50.0)
    {
        var text = string.Join("\r\n", new[]
        {
            "root\\app\\amp\\on_off:{\"value\":\"ON\"}",
            "root\\app\\amp\\amp:{\"value\":\"TestAmp\"}",
            "root\\app\\amp\\gain:{\"value\":0.000000}",
            $"root\\app\\amp\\vol:{{\"value\":{ampVol:F6}}}",
            $"root\\app\\eq\\level:{{\"value\":{eqLevel:F6}}}",
            $"root\\app\\output\\pst\\level:{{\"value\":{pstLevel:F6}}}",
        });
        var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return Sonulab.Core.Model.PresetDocument.Parse(blob);
    }

    // A .pst with its Modulation block ON. `mod` is outside Blocks_InScope, so nothing the editor
    // builds from AllFields() ever carries this path — only reading the .pst directly can see it.
    static Sonulab.Core.Model.PresetDocument PstWithModOn()
    {
        var text = string.Join("\r\n", new[]
        {
            "root\\app\\amp\\on_off:{\"value\":\"ON\"}",
            "root\\app\\amp\\amp:{\"value\":\"TestAmp\"}",
            "root\\app\\amp\\gain:{\"value\":0.000000}",
            "root\\app\\amp\\vol:{\"value\":50.000000}",
            "root\\app\\eq\\level:{\"value\":0.000000}",
            "root\\app\\output\\pst\\level:{\"value\":0.000000}",
            "root\\app\\mod\\on_off:{\"value\":\"ON\"}",
        });
        var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return Sonulab.Core.Model.PresetDocument.Parse(blob);
    }

    // A .pst that names an amp NOT on the device's root\amp list (Dev() only seeds "TestAmp") —
    // an orphaned reference, e.g. the amp was deleted or renamed since this preset was captured.
    static Sonulab.Core.Model.PresetDocument PstNamingMissingAmp()
    {
        var text = string.Join("\r\n", new[]
        {
            "root\\app\\amp\\on_off:{\"value\":\"ON\"}",
            "root\\app\\amp\\amp:{\"value\":\"GhostAmp\"}",
            "root\\app\\amp\\gain:{\"value\":0.000000}",
            "root\\app\\amp\\vol:{\"value\":50.000000}",
            "root\\app\\eq\\level:{\"value\":6.000000}",
            "root\\app\\output\\pst\\level:{\"value\":0.000000}",
        });
        var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
        return Sonulab.Core.Model.PresetDocument.Parse(blob);
    }

    static byte[] FlatAmpSlot()
    {
        var pre = new float[1024]; pre[0] = 1f;
        var g2 = new float[1024]; g2[0] = 1f;
        return Sonulab.Distill.VxampCodec.Encode(new Sonulab.Distill.WhTensors(
            pre, Sonulab.Distill.VxampFormat.G2HeaderFloats(), g2,
            Sonulab.Distill.VxampFormat.NlmixHeaderFloats(), 0f));
    }

    static ParameterEditorViewModel Vm(FakeSonuLink d, FakeStatusService status,
                                       Sonulab.Core.Model.PresetDocument targetPst,
                                       Action? onAmpRead = null) =>
        new(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            ParameterExposure.Default,
            status: status,
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => { onAmpRead?.Invoke(); return Task.FromResult(FlatAmpSlot()); },
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => Task.FromResult(targetPst));

    [Fact]
    public async Task Matching_a_louder_preset_proposes_that_many_db()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(6.0, vm.Blocks[0].Fields.First().Number, 3);
        Assert.True(vm.IsDirty);                       // proposed, NOT written
        Assert.DoesNotContain(d.CommandLog, c => c.StartsWith(@"write root\app\output\pst\level:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_targets_own_trim_is_carried_into_the_proposal()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0, pstLevel: -2.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(4.0, vm.Blocks[0].Fields.First().Number, 3);   // 6 dB louder, trimmed 2 dB down
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(null));

        Assert.Equal(0.0, vm.Blocks[0].Fields.First().Number);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_proposal_beyond_the_range_saturates_and_says_so()
    {
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 19.0, pstLevel: 19.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(20.0, vm.Blocks[0].Fields.First().Number, 3);
        Assert.Contains(status.Succeeded, m => m.Contains("as far as it goes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failing_target_read_reports_and_leaves_the_slider_alone()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: new FakeStatusService(),
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => throw new InvalidOperationException("boom"));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));   // must not throw

        Assert.Equal(0.0, vm.Blocks[0].Fields.First().Number);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task The_amp_volume_flag_is_suppressed_when_both_presets_share_it()
    {
        // Both sides sit at vol = 75 %, so the assumed taper cancels out of the difference.
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 6.0, ampVol: 75.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path == @"root\app\amp\vol").Number = 75.0;

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        // Both: the flag is absent (the thing under test), AND the match actually succeeded —
        // without the second assertion this would pass vacuously if MatchVolumeAsync had thrown
        // before ever calling _status.Success, leaving Succeeded empty either way.
        Assert.DoesNotContain(status.Succeeded, m => m.Contains("Amp Volume", StringComparison.Ordinal));
        Assert.Contains(status.Succeeded, m => m.Contains("Preset Level set to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_amp_volume_flag_surfaces_when_both_are_off_default_but_at_different_values()
    {
        // Both sides are off the 50% schema default, but at DIFFERENT values (75 vs 60) — the
        // assumed taper does NOT cancel here, so the flag must surface, not be suppressed just
        // because both happen to be "off default".
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 6.0, ampVol: 60.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path == @"root\app\amp\vol").Number = 75.0;

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Contains(status.Succeeded, m => m.Contains("Amp Volume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_amp_blob_is_read_once_per_session_not_once_per_estimate()
    {
        var d = Dev(); await d.OpenAsync();
        int reads = 0;
        var vm = Vm(d, new FakeStatusService(), TargetPst(6.0), onAmpRead: () => reads++);
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));
        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        // Four estimates (two matches x two presets), all naming "TestAmp": one 96-chunk read.
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Catalog_version_bump_forces_a_fresh_amp_blob_read()
    {
        var d = Dev(); await d.OpenAsync();
        int reads = 0;
        var catalog = new CatalogVersion();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: new FakeStatusService(),
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            catalog: catalog,
            readAmpBlob: (_, _) => { reads++; return Task.FromResult(FlatAmpSlot()); },
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => Task.FromResult(TargetPst(6.0)));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));
        Assert.Equal(1, reads);

        // The amp was re-uploaded under the same name — the catalog moved, so a stale cached blob
        // must not go on serving the next match.
        catalog.Bump();
        await vm.RefreshRefOptionsAsync();
        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Switching_presets_mid_match_abandons_the_stale_proposal()
    {
        // The target-slot read (readPresetDoc) is gated on a TaskCompletionSource so the test can
        // switch the loaded preset while MatchVolumeAsync is still mid-flight — the same race the
        // real app has since nothing sets IsLoading during a match.
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<Sonulab.Core.Model.PresetDocument>();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: status,
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => { started.TrySetResult(); return release.Task; });
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        var staleField = vm.Blocks[0].Fields.First();

        var match = vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));
        await started.Task;                              // the target-slot read is now stuck

        // The user moves on to another preset before the match finishes.
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "Louder"));

        release.SetResult(TargetPst(eqLevel: 6.0));
        await match;                                      // must not throw

        Assert.Equal(0.0, staleField.Number);              // the abandoned run never wrote to it
        Assert.Equal(0.0, vm.Blocks[0].Fields.First().Number);  // the NEW preset's slider is untouched
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_mod_block_on_the_loaded_preset_surfaces_the_caveat()
    {
        // mod is outside Blocks_InScope, so the editor never builds a field for it — only the
        // .pst document carries it. Before the fix, EstimateLoadedAsync built its dictionary from
        // AllFields() alone and so never saw this path at all (absent reads as OFF to
        // LevelModel), even though the TARGET side (built from the target's own .pst) would
        // correctly see the same flag when it sat on that side of the match instead. The caveat
        // must surface regardless of which side of the match carries it.
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var loadedPst = PstWithModOn();
        var targetPst = TargetPst(eqLevel: 6.0);
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: status,
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (index, _) => Task.FromResult(index == 0 ? loadedPst : targetPst));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Contains(status.Succeeded, m => m.Contains("Modulation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsaved_live_edit_still_wins_over_the_stored_pst_value()
    {
        // The .pst on disk says eq\level = 0 on both sides, but the user nudged the LIVE slider
        // to +4 dB without saving. The estimate must reflect what they are actually hearing, not
        // the stored value — the whole reason EstimateLoadedAsync overlays live fields on top of
        // the .pst base rather than just reading the document as-is.
        var d = Dev(); await d.OpenAsync();
        var loadedPst = TargetPst(eqLevel: 0.0);     // matches the stored/on-disk state
        var targetPst = TargetPst(eqLevel: 0.0);     // flat target: any proposal is purely the live edit
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: new FakeStatusService(),
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            readAmpBlob: (_, _) => Task.FromResult(FlatAmpSlot()),
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (index, _) => Task.FromResult(index == 0 ? loadedPst : targetPst));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        vm.Blocks.SelectMany(b => b.Fields).First(f => f.Path == @"root\app\eq\level").Number = 4.0;

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        // Loaded is +4 dB louder than its own stored .pst (and than the flat target), so matching
        // a flat target must propose -4 dB — proof the live edit, not the stored 0, was used.
        Assert.Equal(-4.0, vm.Blocks[0].Fields.First().Number, 3);
    }

    [Fact]
    public async Task Both_sides_silent_reports_a_failure_instead_of_a_NaN_proposal()
    {
        // amp\vol parked at 0 on both sides mutes the drive signal outright (AmpVolGainDb(0) is
        // -120 dB, comfortably under Loudness' -70 LUFS absolute gate) — both sides estimate as
        // -Infinity, and the match arithmetic computes -Infinity - (-Infinity), which is NaN.
        // Math.Clamp passes NaN straight through, and Save would then write it to the device as
        // malformed JSON, so this must be caught and reported instead.
        var d = DevWithVolAtZero(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, TargetPst(eqLevel: 6.0, ampVol: 0.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));
        Assert.False(vm.IsDirty);   // sanity: loading a preset alone never dirties it

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));

        Assert.Equal(0.0, vm.Blocks[0].Fields.First().Number);   // slider left exactly where it was
        Assert.False(vm.IsDirty);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("silent", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(status.Succeeded);
    }

    [Fact]
    public async Task An_orphaned_amp_reference_is_flagged_rather_than_thrown()
    {
        // The target names an amp that no longer exists on the device (deleted or renamed since
        // the .pst was captured). BlobForAsync's documented contract is to degrade to a flag
        // rather than throw or NRE — nothing pinned that before.
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = Vm(d, status, PstNamingMissingAmp());
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));   // must not throw

        Assert.Contains(status.Succeeded, m => m.Contains("Amp model could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_catalog_bump_noticed_via_a_preset_load_also_clears_the_cache()
    {
        // The bump can be noticed by EITHER RefreshRefOptionsAsync (a Presets-tab revisit) or
        // LoadCoreAsync (loading a DIFFERENT preset — e.g. the IPresetNavigator jump from the
        // Amps tab) — whichever happens first must invalidate the blob cache itself, or the
        // other one's own "version == _optionsVersion" guard sees the version already caught up
        // and never clears for this catalog generation.
        var d = Dev(); await d.OpenAsync();
        int reads = 0;
        var catalog = new CatalogVersion();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()), ParameterExposure.Default,
            status: new FakeStatusService(),
            repo: new Sonulab.Core.Services.DeviceRepository(new SonuClient(d)),
            catalog: catalog,
            readAmpBlob: (_, _) => { reads++; return Task.FromResult(FlatAmpSlot()); },
            readIrBlob: (_, _) => Task.FromResult<byte[]?>(null),
            readPresetDoc: (_, _) => Task.FromResult(TargetPst(6.0)));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(0));
        Assert.Equal(1, reads);

        // The amp was re-uploaded under the same name. Load a DIFFERENT preset — never calling
        // RefreshRefOptionsAsync — since LoadForAsync dedupes by NAME and reselecting "Loaded"
        // would skip LoadCoreAsync (and so this invalidation path) entirely.
        catalog.Bump();
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "Louder"));
        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(0));

        Assert.Equal(2, reads);
    }
}
