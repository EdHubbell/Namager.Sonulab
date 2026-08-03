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

        Assert.Equal(6.0, vm.Blocks[0].Fields[0].Number, 3);
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

        Assert.Equal(4.0, vm.Blocks[0].Fields[0].Number, 3);   // 6 dB louder, trimmed 2 dB down
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var d = Dev(); await d.OpenAsync();
        var vm = Vm(d, new FakeStatusService(), TargetPst(eqLevel: 6.0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Loaded"));

        await vm.MatchVolumeAsync(() => Task.FromResult<int?>(null));

        Assert.Equal(0.0, vm.Blocks[0].Fields[0].Number);
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

        Assert.Equal(20.0, vm.Blocks[0].Fields[0].Number, 3);
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

        Assert.Equal(0.0, vm.Blocks[0].Fields[0].Number);
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

        Assert.DoesNotContain(status.Succeeded, m => m.Contains("Amp Volume", StringComparison.Ordinal));
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
        var staleField = vm.Blocks[0].Fields[0];

        var match = vm.MatchVolumeAsync(() => Task.FromResult<int?>(1));
        await started.Task;                              // the target-slot read is now stuck

        // The user moves on to another preset before the match finishes.
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(1, "Louder"));

        release.SetResult(TargetPst(eqLevel: 6.0));
        await match;                                      // must not throw

        Assert.Equal(0.0, staleField.Number);              // the abandoned run never wrote to it
        Assert.Equal(0.0, vm.Blocks[0].Fields[0].Number);  // the NEW preset's slider is untouched
        Assert.False(vm.IsDirty);
    }
}
