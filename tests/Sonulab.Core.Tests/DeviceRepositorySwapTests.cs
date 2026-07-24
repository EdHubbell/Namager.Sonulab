using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class DeviceRepositorySwapTests
{
    static FakePresetDevice Dev()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        return d;
    }
    static DeviceRepository Repo(FakePresetDevice d) => new(new SonuClient(d));

    [Fact] public async Task Swap_exchanges_name_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await r.SwapPresetSlotsAsync(0, 1);
        var names = (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
        Assert.Equal("B", names[0]);
        Assert.Equal("A", names[1]);
        Assert.Equal("\"mB\"", (await r.ReadPresetAsync(0)).GetValueJson(@"root\app\amp\amp"));
        Assert.Equal("\"mA\"", (await r.ReadPresetAsync(1)).GetValueJson(@"root\app\amp\amp"));
    }

    [Fact] public async Task Swap_with_empty_slot_moves_preset_and_empties_source()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await r.SwapPresetSlotsAsync(0, 5);   // slot 5 empty
        var names = (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
        Assert.Equal("", names[0]);
        Assert.Equal("A", names[5]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 30)]
    public async Task Swap_rejects_out_of_range_index(int a, int b)
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() => r.SwapPresetSlotsAsync(a, b));
    }
}
