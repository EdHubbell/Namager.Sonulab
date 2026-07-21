using ToneManager.App.ViewModels;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Field crash 2026-07-21 (v0.9.3 test build): the WiFi TCP link died mid-session and the
/// resulting InvalidOperationException("TCP link is not open.") escaped AmpListViewModel.RefreshAsync
/// -> AsyncRelayCommand rethrew on the UI thread -> process death. The v0.9.2 crash-guard only
/// covered PresetListViewModel.RunAsync; these tests pin the guard on EVERY device-facing command
/// entry point: a dead link must surface an error and leave the app alive, never throw.</summary>
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

    private static SonuClient DeadClient() => new(new DeadLink(), readRetryAttempts: 1, readRetryDelayMs: 0);

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
    public async Task Editor_load_on_dead_link_surfaces_error_without_crashing()
    {
        var vm = new ParameterEditorViewModel(DeadClient());
        await vm.LoadCommand.ExecuteAsync(null);             // "Load" button
        Assert.False(vm.IsLoading);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }

    [Fact]
    public async Task Editor_load_for_preset_on_dead_link_surfaces_error_without_crashing()
    {
        var vm = new ParameterEditorViewModel(DeadClient());
        await vm.LoadForCommand.ExecuteAsync("Some Preset"); // fired on preset selection
        Assert.False(vm.IsLoading);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
    }
}
