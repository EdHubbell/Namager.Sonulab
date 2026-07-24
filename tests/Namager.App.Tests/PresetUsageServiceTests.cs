// tests/Namager.App.Tests/PresetUsageServiceTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageServiceTests
{
    private const string AmpNode = @"root\app\amp\amp:{{""value"":""{0}""}}";
    private static string Amp(string name) => string.Format(AmpNode, name);

    private static (PresetUsageService svc, DeviceRepository repo, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        // slot 2 empty on purpose
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        return (new PresetUsageService(repo), repo, dev);
    }

    [Fact]
    public async Task GetAsync_builds_the_map_from_occupied_presets_with_slots()
    {
        var (svc, _, _) = Make();
        var map = await svc.GetAsync();
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task GetAsync_caches_and_does_not_reread_until_invalidated()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        await dev.OpenAsync();
        var link = new CountingLink(dev);
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(link)));

        await svc.GetAsync();
        int afterFirst = link.Dreads;
        Assert.True(afterFirst > 0, "first build must read preset content");

        await svc.GetAsync();
        Assert.Equal(afterFirst, link.Dreads);          // cache hit: no new reads

        svc.Invalidate();
        await svc.GetAsync();
        Assert.True(link.Dreads > afterFirst);          // rebuild after invalidation
    }

    [Fact]
    public async Task GetAsync_reports_a_status_scope()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        await dev.OpenAsync();
        var status = new FakeStatusService();
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(dev)), status);
        await svc.GetAsync();
        Assert.Contains(status.Begun, m => m.Contains("preset usage"));
    }

    // Counts content reads so we can prove caching.
    private sealed class CountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public CountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }
}
