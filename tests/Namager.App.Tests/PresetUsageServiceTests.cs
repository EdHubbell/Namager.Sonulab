// tests/Namager.App.Tests/PresetUsageServiceTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageServiceTests
{
    private const string AmpNode = @"root\app\amp\amp:{{""value"":""{0}""}}";
    private static string Amp(string name) => string.Format(AmpNode, name);

    private static (PresetUsageService svc, FakePresetDevice dev, CountingLink link) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        // backgroundQuietMs 0: tests exercise scan logic, not the lane (lane has its own tests)
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        return (new PresetUsageService(repo), dev, link);
    }

    [Fact]
    public async Task EnsureComplete_builds_the_full_map()
    {
        var (svc, _, _) = Make();
        var map = await svc.EnsureCompleteAsync();
        Assert.True(svc.IsComplete);
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task Scan_is_progressive_and_raises_MapUpdated_per_preset()
    {
        var (svc, _, _) = Make();
        int updates = 0;
        svc.MapUpdated += () => Interlocked.Increment(ref updates);
        svc.EnsureScanning();
        await svc.EnsureCompleteAsync();
        Assert.True(updates >= 2, $"expected per-preset updates, got {updates}");
        Assert.Single(svc.Current.PresetsUsingAmp("Plexi").Where(r => r.Index == 0));
    }

    [Fact]
    public async Task Complete_map_is_cached_until_invalidated()
    {
        var (svc, _, link) = Make();
        await svc.EnsureCompleteAsync();
        int afterFirst = link.Dreads;
        Assert.True(afterFirst > 0);

        await svc.EnsureCompleteAsync();
        Assert.Equal(afterFirst, link.Dreads);              // cache hit

        svc.Invalidate();
        Assert.False(svc.IsComplete);
        Assert.NotSame(PresetUsageMap.Empty, svc.Current);  // stale map kept for highlights
        await svc.EnsureCompleteAsync();
        Assert.True(link.Dreads > afterFirst);              // rescan happened
    }

    [Fact]
    public async Task Invalidate_during_a_scan_restarts_it()
    {
        var (svc, dev, _) = Make();
        svc.EnsureScanning();
        svc.Invalidate();
        dev.SeedSlot(2, "New", new[] { Amp("JCM800") });
        var map = await svc.EnsureCompleteAsync();
        Assert.Single(map.PresetsUsingAmp("JCM800"));       // post-invalidate content included
    }

    [Fact]
    public async Task EnsureComplete_throws_when_the_link_is_dead_and_guards_stay_closed()
    {
        var dev = new FakePresetDevice();                   // never opened → SendAsync throws
        var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
        Assert.False(svc.IsComplete);
    }

    [Fact]
    public async Task Stop_cancels_a_running_scan()
    {
        var (svc, _, _) = Make();
        svc.EnsureScanning();
        svc.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
    }

    [Fact]
    public async Task Torn_preset_read_aborts_the_scan_and_guards_stay_closed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new TornIndexLink(dev, tearIndex: 1);
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);

        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
        Assert.False(svc.IsComplete);
    }

    /// <summary>Link wrapper that returns an empty response ("torn read") for any dread targeting
    /// one specific preset index, and delegates everything else.</summary>
    private sealed class TornIndexLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        private readonly int _tearIndex;
        public TornIndexLink(Sonulab.Core.Transport.ISonuLink inner, int tearIndex)
        {
            _inner = inner;
            _tearIndex = tearIndex;
        }
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)
                && command.Contains($"\"index\":{_tearIndex}", StringComparison.Ordinal))
                return Task.FromResult("");
            return _inner.SendAsync(command, ct);
        }
    }

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
