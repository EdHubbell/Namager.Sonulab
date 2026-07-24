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
