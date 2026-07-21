using System.Net;
using ToneManager.App.Services;
using ToneManager.App.ViewModels;

public class UpdateCheckServiceTests
{
    // ---------- pure version compare ----------
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("1.0.0-dev", "9.9.9", false)]   // dev builds never prompt
    [InlineData("1.0.0", "garbage", false)]     // malformed latest -> never prompt
    [InlineData("garbage", "1.0.0", false)]     // malformed current -> never prompt
    public void IsNewer_compares_correctly(string current, string latest, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(current, latest));

    // ---------- HTTP behavior via fake handler ----------
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body) });
    }

    private const string ReleaseJson =
        """{"tag_name":"v2.5.0","html_url":"https://github.com/EdHubbell/StompStationManager/releases/tag/v2.5.0"}""";

    [Fact]
    public async Task CheckAsync_returns_update_when_newer()
    {
        var svc = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson), "1.0.0");
        var info = await svc.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal("2.5.0", info!.Version);
        Assert.Contains("/releases/tag/v2.5.0", info.Url);
    }

    [Fact]
    public async Task CheckAsync_returns_null_when_current_is_latest()
    {
        var svc = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson), "2.5.0");
        Assert.Null(await svc.CheckAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "rate limited")]
    [InlineData(HttpStatusCode.NotFound, "no releases yet")]
    public async Task CheckAsync_swallows_all_failures(HttpStatusCode status, string body)
    {
        var svc = new UpdateCheckService(new FakeHandler(status, body), "1.0.0");
        Assert.Null(await svc.CheckAsync());   // must not throw either
    }

    // ---------- MainWindowViewModel wiring ----------
    private sealed class FakeUpdateCheck(UpdateInfo? result) : IUpdateCheckService
    {
        public Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) => Task.FromResult(result);
    }

    [Fact]
    public async Task ViewModel_sets_banner_when_update_found_and_dismiss_clears_it()
    {
        var vm = new MainWindowViewModel();
        await vm.CheckForUpdatesAsync(new FakeUpdateCheck(new UpdateInfo("2.0.0", "https://example.test/rel")));
        Assert.NotNull(vm.UpdateAvailable);
        vm.DismissUpdateCommand.Execute(null);
        Assert.Null(vm.UpdateAvailable);
    }

    [Fact]
    public async Task ViewModel_stays_quiet_when_no_update()
    {
        var vm = new MainWindowViewModel();
        await vm.CheckForUpdatesAsync(new FakeUpdateCheck(null));
        Assert.Null(vm.UpdateAvailable);
    }
}
