using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

public class PresetHeadReadTests
{
    private sealed class CountingLink : ISonuLink
    {
        private readonly ISonuLink _inner;
        public int Dreads;
        public CountingLink(ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }

    private static IReadOnlyList<string> RealDocLines()
    {
        var blob = File.ReadAllBytes(Path.Combine("Fixtures", "QuadReverbSM57.pst"));
        return PresetDocument.Parse(blob).Lines;
    }

    private static (DeviceRepository repo, CountingLink link, FakePresetDevice dev) Make()
    {
        var dev = new FakePresetDevice();
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        return (new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0)), link, dev);
    }

    [Fact]
    public async Task Head_read_of_a_real_document_finds_all_refs_within_the_cap()
    {
        var (repo, link, dev) = Make();
        dev.SeedSlot(0, "Quad", RealDocLines());
        var doc = await repo.ReadPresetHeadAsync(0);

        var map = PresetUsageMap.Build(new[] { (0, "Quad", doc) });
        Assert.Single(map.PresetsUsingAmp("Quad Reverb Randall Head SM57"));
        Assert.Single(map.PresetsUsingIr("TWIN REVERB __ CLEAN"));

        // THE bounded-cost assertion (handoff step 4): the real doc's last ref line (ir2\ir)
        // sits in chunk 23 — the head read must stop right there, way under the full 64.
        Assert.InRange(link.Dreads, 1, DeviceRepository.HeadChunkCap);
        Assert.True(link.Dreads < 30, $"expected an early stop, read {link.Dreads} chunks");
    }

    [Fact]
    public async Task Head_read_stops_at_content_end_for_a_short_document()
    {
        var (repo, link, dev) = Make();
        dev.SeedSlot(0, "Tiny", new[]
        {
            @"root\app\amp\amp:{""value"":""Plexi""}",
            @"root\app\ir\ir:{""value"":""V30""}",
        });   // ~70 bytes → content ends inside chunk 1
        var doc = await repo.ReadPresetHeadAsync(0);
        Assert.Single(PresetUsageMap.Build(new[] { (0, "Tiny", doc) }).PresetsUsingAmp("Plexi"));
        Assert.Equal(1, link.Dreads);                       // NUL seen in the first chunk → stop
    }

    [Fact]
    public async Task Head_read_falls_back_to_a_full_read_when_refs_never_complete()
    {
        var (repo, link, dev) = Make();
        // A document with NO ir2 line, sized to fill chunks 1..32 (the head window) with no NUL
        // byte so the loop never stops early, and HeadComplete never true (no ir/ir2 lines) — this
        // forces the HeadChunkCap fallback. 160 filler lines + the amp line = 8034 bytes: comfortably
        // more than the 4096 bytes the head window (chunks 1..32) covers, and under the fake
        // device's fixed 8192-byte slot buffer (FakePresetDevice.PresetDocumentFrom throws if
        // content overflows it).
        var filler = Enumerable.Range(0, 160)
            .Select(i => $@"root\app\mod\rate\rawdata{i:D3}:{{""value"":1.0000000}}");
        dev.SeedSlot(0, "Odd", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" }.Concat(filler));
        var doc = await repo.ReadPresetHeadAsync(0);
        Assert.Equal(64, link.Dreads);                      // cap hit → full-document fallback
        Assert.Single(PresetUsageMap.Build(new[] { (0, "Odd", doc) }).PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task Background_list_read_returns_slots()
    {
        var (repo, _, dev) = Make();
        dev.SeedSlot(3, "Lead", new[] { @"root\app\amp\amp:{""value"":""Plexi""}" });
        var slots = await repo.ListPresetsBackgroundAsync();
        Assert.Equal(30, slots.Count);
        Assert.Equal("Lead", slots[3].Name);
        Assert.True(slots[0].IsEmpty);
    }
}
