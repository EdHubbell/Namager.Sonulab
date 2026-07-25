using Namager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Field crash 2026-07-21 (v0.9.3 test build): the WiFi TCP link died mid-session and the
/// resulting InvalidOperationException("TCP link is not open.") escaped AmpListViewModel.RefreshAsync
/// -> AsyncRelayCommand rethrew on the UI thread -> process death. The v0.9.2 crash-guard only
/// covered PresetListViewModel.RunAsync; these tests drive the device-facing command entry points
/// (refresh/load on all tabs, editor load/save, both uploads, the amp details read) with a dead
/// link: each must surface an error and leave the app alive, never throw. Anything NOT covered
/// here should be assumed unguarded until proven otherwise — that assumption failing twice is
/// exactly how the field crashes happened.</summary>
public class CrashGuardTests
{
    /// <summary>A link whose connection has died — throws exactly what TcpSonuLink throws.</summary>
    private sealed class DeadLink : ISonuLink
    {
        public bool IsOpen => false;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
            => throw new InvalidOperationException("TCP link is not open.");
    }

    /// <summary>Healthy fake device until <see cref="Dead"/> is set — models the link dying
    /// MID-SESSION (after lists were populated), the actual field scenario.</summary>
    private sealed class KillableLink(ISonuLink inner) : ISonuLink
    {
        public bool Dead;
        public bool IsOpen => inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => inner.OpenAsync(ct);
        public void Close() => inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
            => Dead ? throw new InvalidOperationException("TCP link is not open.") : inner.SendAsync(command, ct);
    }

    /// <summary>Lets the preset SELECT write (<c>write root\app\preset:…</c>) succeed but fails the
    /// content BROWSE (<c>browse root\app</c>) that follows it — isolates a failure inside
    /// <c>LoadCoreAsync</c> (post-select, while reading preset content) from a failure at selection
    /// itself. <c>LoadForAsync</c> does select-then-browse in that order, so a link that fails on
    /// EVERY send (like <see cref="DeadLink"/>) can only ever exercise the select-failure path.</summary>
    private sealed class BrowseFailsLink : ISonuLink
    {
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default)
            => command.StartsWith("browse ", StringComparison.Ordinal)
                ? throw new InvalidOperationException("TCP link is not open.")
                : Task.FromResult("");
    }

    private static SonuClient DeadClient() => new(new DeadLink(), readRetryAttempts: 1, readRetryDelayMs: 0);

    private static string TempBlobFile(string name, int bytes)
    {
        var p = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(p, Enumerable.Repeat((byte)0xEE, bytes).ToArray());
        return p;
    }

    [Fact]
    public async Task Amp_refresh_on_dead_link_surfaces_error_without_crashing()
    {
        var svc = new AmpService(DeadClient(), Path.Combine(Path.GetTempPath(), "cg-amp"), paceMs: 0, settleMs: 0);
        var vm = new AmpListViewModel(svc, writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);          // must NOT throw (crashed the app in the field)
        Assert.False(vm.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Ir_refresh_on_dead_link_surfaces_error_without_crashing()
    {
        var svc = new IrService(DeadClient(), Path.Combine(Path.GetTempPath(), "cg-ir"));
        var vm = new IrListViewModel(svc, writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Preset_refresh_on_dead_link_surfaces_error_without_crashing()
    {
        var repo = new DeviceRepository(DeadClient());
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Editor_load_content_on_dead_link_surfaces_error_without_crashing()
    {
        // The select write succeeds; the browse that reads preset CONTENT is what fails here —
        // proves a failure inside LoadCoreAsync surfaces as ErrorMessage instead of escaping the
        // [RelayCommand]. (Failure at selection itself is the adjacent test below.)
        var vm = new ParameterEditorViewModel(new SonuClient(new BrowseFailsLink(), readRetryAttempts: 1, readRetryDelayMs: 0));
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "P"));
        Assert.False(vm.IsLoading);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Editor_load_for_preset_on_dead_link_surfaces_error_without_crashing()
    {
        var vm = new ParameterEditorViewModel(DeadClient());
        await vm.LoadForCommand.ExecuteAsync(new PresetTarget(0, "Some Preset")); // fired on preset selection
        Assert.False(vm.IsLoading);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Amp_upload_when_link_dies_mid_session_surfaces_error_without_crashing()
    {
        // Review finding on the first sweep: StartUploadAsync had only typed catches — the
        // longest-running device op let the same transport exception escape.
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        var link = new KillableLink(dev);
        await link.OpenAsync();
        var svc = new AmpService(new SonuClient(link), Path.Combine(Path.GetTempPath(), $"cg-au-{Guid.NewGuid():N}"), paceMs: 0, settleMs: 0);
        var vm = new AmpListViewModel(svc, writesAllowed: true, dispatch: a => a());
        await vm.RefreshCommand.ExecuteAsync(null);                        // healthy: lists populate
        vm.BeginUploadCommand.Execute(TempBlobFile("cg.vxamp", 12288));    // vxamp: no distillation
        link.Dead = true;                                                  // the link dies now
        await vm.StartUploadCommand.ExecuteAsync(null);                    // must NOT throw
        Assert.False(vm.IsUploading);
        Assert.False(string.IsNullOrWhiteSpace(vm.UploadError));
    }

    [Fact]
    public async Task Ir_upload_when_link_dies_mid_session_surfaces_error_without_crashing()
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "Spring", Enumerable.Repeat((byte)1, 4096).ToArray());
        var link = new KillableLink(dev);
        await link.OpenAsync();
        var svc = new IrService(new SonuClient(link), Path.Combine(Path.GetTempPath(), $"cg-iu-{Guid.NewGuid():N}"), paceMs: 0, settleMs: 0);
        var vm = new IrListViewModel(svc, writesAllowed: true, convertWav: _ => Enumerable.Repeat((byte)0xC0, 4096).ToArray());
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.BeginUploadCommand.Execute(TempBlobFile("cg.wav", 4096));
        link.Dead = true;
        await vm.StartUploadCommand.ExecuteAsync(null);
        Assert.False(vm.IsUploading);
        Assert.False(string.IsNullOrWhiteSpace(vm.UploadError));
    }

    [Fact]
    public async Task Amp_details_read_when_link_dies_mid_session_surfaces_error_without_crashing()
    {
        // The details read runs on selection (and is awaited by SaveMetadataAsync's tail) — an
        // escape here is the same unhandled-rethrow class.
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        var link = new KillableLink(dev);
        await link.OpenAsync();
        var svc = new AmpService(new SonuClient(link), Path.Combine(Path.GetTempPath(), $"cg-ad-{Guid.NewGuid():N}"), paceMs: 0, settleMs: 0);
        var vm = new AmpListViewModel(svc, writesAllowed: true, dispatch: a => a());
        await vm.RefreshCommand.ExecuteAsync(null);
        link.Dead = true;
        vm.Selected = vm.Items[0];                                         // triggers the details read
        if (vm.DetailsLoadTask is { } t) await t;                          // must NOT throw
        Assert.False(string.IsNullOrWhiteSpace(vm.DetailsError));
    }
}
