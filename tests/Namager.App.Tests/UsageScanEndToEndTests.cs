// tests/Namager.App.Tests/UsageScanEndToEndTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

/// <summary>The handoff's step-4 acceptance: with a REAL preset document, the scan resolves a
/// used-highlight within the head-read budget (≤32 dreads/preset) and the map is correct.</summary>
public class UsageScanEndToEndTests
{
    [Fact]
    public async Task Scan_of_a_real_document_is_bounded_and_correct()
    {
        var dev = new FakePresetDevice();   // linked from Sonulab.Core.Tests (see Step 2)
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        dev.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
        await dev.OpenAsync();
        var counter = new CountingLink(dev);
        var svc = new PresetUsageService(
            new DeviceRepository(new SonuClient(counter, backgroundQuietMs: 0)));

        var map = await svc.EnsureCompleteAsync();

        Assert.Single(map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Single(map.PresetsUsingIr("TWIN REVERB __ CLEAN"));
        Assert.InRange(counter.Dreads, 1, DeviceRepository.HeadChunkCap);
    }

    [Fact]
    public async Task Warm_start_over_a_real_document_costs_zero_dreads_then_verifies_within_budget()
    {
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        var cachePath = Path.Combine(Path.GetTempPath(), $"nmgr-e2e-{Guid.NewGuid():N}.json");
        try
        {
            // First connection: scan to completion, which persists the cache.
            var dev1 = new FakePresetDevice();
            dev1.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
            await dev1.OpenAsync();
            var svc1 = new PresetUsageService(
                new DeviceRepository(new SonuClient(dev1, backgroundQuietMs: 0)), "dev-1", cachePath);
            await svc1.EnsureCompleteAsync();

            // Simulated reconnect: fresh device, fresh counting link, same cache.
            var dev2 = new FakePresetDevice();
            dev2.SeedSlot(0, "Quad Reverb SM57", PresetDocument.Parse(blob).Lines);
            await dev2.OpenAsync();
            var counter = new CountingLink(dev2);
            var svc2 = new PresetUsageService(
                new DeviceRepository(new SonuClient(counter, backgroundQuietMs: 0)), "dev-1", cachePath);

            (int Dreads, bool HasAmp)? first = null;
            svc2.MapUpdated += () => first ??= (counter.Dreads,
                svc2.Current.PresetsUsingAmp("Quad Reverb Randall Head SM57").Count == 1);
            await svc2.EnsureCompleteAsync();

            Assert.Equal(0, first!.Value.Dreads);
            Assert.True(first.Value.HasAmp);
            Assert.InRange(counter.Dreads, 1, DeviceRepository.HeadChunkCap);
        }
        finally { File.Delete(cachePath); }
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
