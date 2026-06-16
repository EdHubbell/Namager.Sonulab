using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class DeviceRepositoryTests
{
    static async Task<(DeviceRepository repo, FakePresetDevice dev)> Repo()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(0, "Alpha", new[] { @"root\app\amp\amp:{""value"":""AmpA""}" });
        d.SeedSlot(1, "Beta", new[] { @"root\app\amp\amp:{""value"":""AmpB""}" });
        await d.OpenAsync();
        return (new DeviceRepository(new SonuClient(d)), d);
    }

    [Fact] public async Task ListPresets_returns_30_slots_with_names_and_emptiness()
    {
        var (repo, _) = await Repo();
        var slots = await repo.ListPresetsAsync();
        Assert.Equal(30, slots.Count);
        Assert.Equal(0, slots[0].Index);
        Assert.Equal("Alpha", slots[0].Name);
        Assert.False(slots[0].IsEmpty);
        Assert.True(slots[2].IsEmpty);
    }
}
