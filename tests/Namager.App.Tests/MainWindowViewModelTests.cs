using Namager.App.ViewModels;
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

public class MainWindowViewModelTests
{
    private static AmpListViewModel AmpVm(out FakeAmpDevice dev)
    {
        dev = new FakeAmpDevice();
        dev.SeedAmp(0, "A", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), Path.Combine(Path.GetTempPath(), "mwvm-t"), 0, 0);
        return new AmpListViewModel(svc, writesAllowed: true);
    }

    /// <summary>C1 test seam: gates the "read root\amp" list read behind a TaskCompletionSource so
    /// the first-visit refresh started by EnsureTabLoaded stays genuinely in-flight (a real await
    /// suspension, not one that completes synchronously like the plain fakes do) until the test
    /// releases it.</summary>
    private sealed class GatedAmpDevice : FakeAmpDevice
    {
        public readonly TaskCompletionSource Gate = new();
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command == @"read root\amp") await Gate.Task;
            return await base.SendAsync(command, ct);
        }
    }

    [Fact]
    public void EnsureTabLoaded_refreshes_amps_once_on_first_visit_only()
    {
        var vm = new MainWindowViewModel();
        vm.Amps = AmpVm(out var dev);
        int Reads() => dev.CommandLog.Count(c => c == @"read root\amp");

        Assert.Equal(0, Reads());                 // constructing the VM must not read the device
        vm.EnsureTabLoaded(1);
        Assert.Equal(1, Reads());                 // first visit loads
        vm.EnsureTabLoaded(1);
        vm.EnsureTabLoaded(0);
        vm.EnsureTabLoaded(1);
        Assert.Equal(1, Reads());                 // revisits do not reload (manual Refresh still can)
    }

    [Fact]
    public void EnsureTabLoaded_ignores_missing_vms_and_presets_index()
    {
        var vm = new MainWindowViewModel();
        vm.EnsureTabLoaded(0);                    // presets tab: no-op here (eager elsewhere)
        vm.EnsureTabLoaded(1);                    // Amps is null before connect: must not throw
        vm.EnsureTabLoaded(2);
    }

    [Fact]
    public void Tone3000_tab_exists_from_construction_without_a_device()
    {
        var vm = new MainWindowViewModel();
        Assert.NotNull(vm.Tone3000);
        Assert.False(vm.Tone3000.IsDeviceReady);             // no device yet
    }

    [Fact]
    public void NavigateToUpload_for_amp_prefills_and_navigates()
    {
        var vm = new MainWindowViewModel();
        int? navigatedTo = null;
        vm.NavigateRequested += i => navigatedTo = i;
        // No device connected: must not throw, must not navigate.
        vm.NavigateToUpload(isIr: false, path: "x.nam", notes: "n", url: "u");
        Assert.Null(navigatedTo);
        Assert.NotNull(vm.Tone3000.Banner);                  // told the user why nothing happened
    }

    /// <summary>C1: a Tone3000 send-to-pedal that arrives before the Amps tab has ever been
    /// visited fires NavigateRequested, which (per MainWindow.axaml.cs's nav-changed handler)
    /// triggers EnsureTabLoaded's first-visit refresh. NavigateToUploadAsync must await that
    /// in-flight refresh (via PendingTabLoad) BEFORE calling BeginUploadPrefilled — otherwise
    /// BeginUploadPrefilled runs while Amps.IsBusy is still true, CanMutate is false, and it
    /// silently no-ops (the panel never opens).</summary>
    [Fact]
    public async Task NavigateToUpload_waits_for_the_first_visit_tab_load()
    {
        var dev = new GatedAmpDevice();
        dev.SeedAmp(0, "A", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), Path.Combine(Path.GetTempPath(), "mwvm-t2"), 0, 0);
        var amps = new AmpListViewModel(svc, writesAllowed: true);

        var vm = new MainWindowViewModel { Amps = amps };
        // Mirror MainWindow.axaml.cs's OnNavSelectionChanged: NavigateRequested drives EnsureTabLoaded.
        vm.NavigateRequested += i => vm.EnsureTabLoaded(i);

        var handoff = vm.NavigateToUploadAsync(isIr: false, path: "x.nam", notes: "n", url: "u");

        // The first-visit refresh is genuinely in flight (gated); the guard that causes C1 must
        // be observably armed right now — BeginUploadPrefilled must NOT have run yet.
        Assert.True(amps.IsBusy);
        Assert.False(amps.IsUploadPanelOpen);

        dev.Gate.SetResult();
        await handoff;

        Assert.False(amps.IsBusy);
        Assert.True(amps.IsUploadPanelOpen);                 // now prefilled, after the load completed
        Assert.Equal("n", amps.UploadNotes);
        Assert.Equal("u", amps.UploadUrl);
    }

    [Fact]
    public async Task EnsureTabLoaded_reapplies_amp_usage_on_revisit()
    {
        // A usage service whose map changes; revisiting the Amps tab must re-apply it without
        // re-listing amps (mirrors: user edits presets, returns to the Amps tab).
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), Path.Combine(Path.GetTempPath(), "mwvm-usage"), 0, 0);
        var usage = new FakePresetUsageService();
        var amps = new AmpListViewModel(svc, writesAllowed: true, usage: usage);

        var vm = new MainWindowViewModel { Amps = amps };
        vm.EnsureTabLoaded(1);                          // first visit: full refresh
        if (vm.PendingTabLoad is { } t1) await t1;
        Assert.False(amps.Items[0].IsUsed);

        usage.Map = FakePresetUsageService.MapFor((6, "Lead", new[] { FakePresetUsageService.AmpLine("Clean") }));
        vm.EnsureTabLoaded(1);                          // revisit: re-apply usage
        if (vm.PendingTabLoad is { } t2) await t2;

        Assert.True(amps.Items[0].IsUsed);
    }

    // Editor over a fake link with one amp-ref field, sharing `catalog` with the caller.
    private static ParameterEditorViewModel EditorVm(CatalogVersion catalog, out FakeSonuLink dev)
    {
        var d = new FakeSonuLink();
        d.SeedBrowse(@"root\app",
            "root\\app\\amp\\amp:{\"desc\":\"Model\",\"value\":\"mA\",\"type\":\"plist\",\"ref\":\"root\\\\amp\"}");
        d.SeedList(@"root\amp", new[] { "mA", "mB" });
        d.OpenAsync().GetAwaiter().GetResult();
        dev = d;
        return new ParameterEditorViewModel(new SonuClient(d), catalog: catalog);
    }

    [Fact] public void EnsureTabLoaded_zero_is_a_noop_when_no_editor_exists()
    {
        var vm = new MainWindowViewModel();
        vm.EnsureTabLoaded(0);                   // not connected: must not throw
        Assert.Null(vm.Editor);
    }

    [Fact] public async Task EnsureTabLoaded_zero_rereads_the_amp_list_after_a_catalog_bump()
    {
        var catalog = new CatalogVersion();
        var editor = EditorVm(catalog, out var dev);
        var vm = new MainWindowViewModel { Editor = editor };
        await editor.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));

        int Reads() => dev.CommandLog.Count(c => c == @"read root\amp");
        int afterLoad = Reads();

        vm.EnsureTabLoaded(0);
        if (vm.PendingTabLoad is { } t1) await t1;
        Assert.Equal(afterLoad, Reads());        // catalog unmoved: no extra device traffic

        catalog.Bump();
        vm.EnsureTabLoaded(0);
        if (vm.PendingTabLoad is { } t2) await t2;
        Assert.Equal(afterLoad + 1, Reads());    // bumped: exactly one re-read
    }

    static string TempSettings() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"), "settings.json");

    [Fact] public void SetTheme_updates_the_selected_flags_and_persists()
    {
        var path = TempSettings();
        var vm = new MainWindowViewModel(path);
        vm.SetThemeCommand.Execute("Dark");

        Assert.Equal("Dark", vm.Theme);
        Assert.True(vm.IsThemeDark);
        Assert.False(vm.IsThemeLight);
        Assert.False(vm.IsThemeSystem);
        Assert.Equal("Dark", Namager.App.Services.AppSettingsStore.Load(path).Theme);
    }

    [Fact] public void SetTheme_ignores_an_unknown_value()
    {
        var path = TempSettings();
        var vm = new MainWindowViewModel(path);
        vm.SetThemeCommand.Execute("Chartreuse");
        Assert.Equal("System", vm.Theme);
        Assert.False(System.IO.File.Exists(path));      // nothing persisted for a rejected value
    }

    [Fact] public void A_new_view_model_starts_from_the_persisted_theme()
    {
        var path = TempSettings();
        Namager.App.Services.AppSettingsStore.Save(
            new Namager.App.Services.AppSettings { Theme = "Light" }, path);
        Assert.Equal("Light", new MainWindowViewModel(path).Theme);
    }

    [Fact] public void NewBackupFolder_is_a_timestamped_folder_under_Documents()
    {
        var folder = MainWindowViewModel.NewBackupFolder(new System.DateTime(2026, 7, 25, 14, 3, 7));
        Assert.EndsWith(System.IO.Path.Combine("NAMager Backups", "2026-07-25 140307"), folder);
        Assert.StartsWith(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), folder);
    }

    [Fact] public void NewBackupFolder_gives_distinct_folders_for_times_one_second_apart()
    {
        var t = new System.DateTime(2026, 7, 25, 14, 3, 7);
        var first = MainWindowViewModel.NewBackupFolder(t);
        var second = MainWindowViewModel.NewBackupFolder(t.AddSeconds(1));
        Assert.NotEqual(first, second);
    }

    [Fact] public async Task BackupPresets_returns_null_when_not_connected()
    {
        var vm = new MainWindowViewModel();
        Assert.Null(await vm.BackupPresetsAsync());
    }

    // ---- FIX 2: Backup/Restore re-entrancy guard ----

    [Fact] public void CanBackup_and_CanRestore_react_to_connection_and_in_flight_state()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.CanBackup);
        Assert.False(vm.CanRestore);

        vm.Connection.IsConnected = true;
        Assert.True(vm.CanBackup);
        Assert.False(vm.CanRestore);           // writes not (yet) allowed

        vm.Connection.WritesAllowed = true;
        Assert.True(vm.CanRestore);

        vm.FileOperationInFlight = true;
        Assert.False(vm.CanBackup);
        Assert.False(vm.CanRestore);

        vm.FileOperationInFlight = false;
        Assert.True(vm.CanBackup);
        Assert.True(vm.CanRestore);
    }

    [Fact] public void FileOperationInFlight_change_re_notifies_CanBackup_and_CanRestore()
    {
        var vm = new MainWindowViewModel();
        vm.Connection.IsConnected = true;
        vm.Connection.WritesAllowed = true;
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.FileOperationInFlight = true;

        Assert.Contains(nameof(MainWindowViewModel.CanBackup), raised);
        Assert.Contains(nameof(MainWindowViewModel.CanRestore), raised);
    }

    [Fact] public async Task BackupPresetsAsync_clears_FileOperationInFlight_even_when_disconnected()
    {
        // Disconnected VM: BackupPresetsAsync returns null via its early-return path. The flag
        // must still come back down — a try/finally around the WHOLE method, not just the
        // device-contacting branch.
        var vm = new MainWindowViewModel();
        Assert.Null(await vm.BackupPresetsAsync());
        Assert.False(vm.FileOperationInFlight);
    }

    // ---- Task 7: Export/Import Snapshot (import only here — export needs a live device, see
    // SnapshotExportImportTests) ----

    private static byte[] SampleIrBlob() => Enumerable.Repeat((byte)0xAB, 4096).ToArray();

    /// <summary>Builds a .namsnap fixture directly through SnapshotArchive (the same writer
    /// ExportSnapshotAsync itself uses), carrying one IR slot with a Tone3000 identity, so
    /// ImportSnapshotAsync has something real to learn from.</summary>
    private static void WriteSampleSnapshot(string path, long irToneId, long irModelId, byte[] irBlob)
    {
        var manifest = new SnapshotManifest(
            SnapshotManifest.CurrentSchema,
            "2026-07-26T14:02:11Z",
            "0.9.7",
            new SnapshotDevice("StompStation", "2.5.1"),
            new[]
            {
                new SnapshotSlot(SnapshotSlotKind.Ir, 0, "IrA", SnapshotArchive.ShaOf(irBlob),
                                  new SnapshotT3k(irToneId, irModelId)),
            });
        var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]> { [(SnapshotSlotKind.Ir, 0)] = irBlob };

        using var fs = File.Create(path);
        SnapshotArchive.Write(fs, manifest, blobs);
    }

    [Fact]
    public async Task Importing_a_snapshot_rebuilds_IR_identities_in_the_index()
    {
        var indexPath = Path.Combine(Path.GetTempPath(), $"ir-idx-{Guid.NewGuid():N}.json");
        var snapPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        try
        {
            WriteSampleSnapshot(snapPath, irToneId: 2468, irModelId: 1357, irBlob: SampleIrBlob());

            var vm = new MainWindowViewModel(settingsPath: null, irIndexPath: indexPath);
            var manifest = await vm.ImportSnapshotAsync(snapPath);

            Assert.Equal(SnapshotManifest.CurrentSchema, manifest.Schema);
            var entry = IrIndex.Load(indexPath).Lookup(IrIndex.ShaOf(SampleIrBlob()));
            Assert.NotNull(entry);
            Assert.Equal(2468, entry!.ToneId);
            Assert.Equal(1357, entry.ModelId);
        }
        finally { File.Delete(indexPath); File.Delete(snapPath); }
    }

    [Fact]
    public async Task Importing_a_damaged_snapshot_surfaces_a_readable_error()
    {
        var snapPath = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.namsnap");
        try
        {
            File.WriteAllText(snapPath, "not a zip");
            var vm = new MainWindowViewModel();

            await Assert.ThrowsAsync<SnapshotArchiveException>(() => vm.ImportSnapshotAsync(snapPath));
        }
        finally { File.Delete(snapPath); }
    }
}
