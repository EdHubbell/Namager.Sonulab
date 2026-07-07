using Sonulab.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
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
}
