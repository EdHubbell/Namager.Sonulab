using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class SlotBlobReorderTests
{
    // Amp-shaped blob device (96 chunks / 12288 B). A 1-byte "blob" content marker per slot is
    // enough to prove content travels with the swap — seed a distinct first byte per slot.
    static (SlotBlobService svc, FakeSlotBlobDevice dev) Amp()
    {
        var dev = new FakeSlotBlobDevice(@"root\amp", 96, 12288);
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new SlotBlobService(new SonuClient(dev), SlotBlobKind.Amp, "backups",
                                      msg => new System.InvalidOperationException(msg));
        return (svc, dev);
    }
    static byte[] Blob(byte marker) { var b = new byte[12288]; b[0] = marker; return b; }

    [Fact] public async Task Swap_exchanges_name_and_content()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        dev.SeedSlot(1, "B", Blob(0xB0));
        await svc.SwapAsync(0, 1);
        Assert.Equal(new[] { "B", "A" }, new[] { dev.SlotNames[0], dev.SlotNames[1] });
        Assert.Equal(0xB0, dev.SlotBlobs[0]![0]);
        Assert.Equal(0xA0, dev.SlotBlobs[1]![0]);
    }

    [Fact] public async Task Swap_with_empty_slot_moves_and_empties_source()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        await svc.SwapAsync(0, 5);
        Assert.Null(dev.SlotNames[0]);
        Assert.Equal("A", dev.SlotNames[5]);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 30)]
    public async Task Swap_rejects_out_of_range(int a, int b)
    {
        var (svc, _) = Amp();
        await Assert.ThrowsAsync<System.InvalidOperationException>(() => svc.SwapAsync(a, b));
    }

    [Fact] public async Task MoveStep_down_reorders_via_swap()
    {
        var (svc, dev) = Amp();
        dev.SeedSlot(0, "A", Blob(0xA0));
        dev.SeedSlot(1, "B", Blob(0xB0));
        await svc.MoveStepAsync(0, up: false);
        Assert.Equal(new[] { "B", "A" }, new[] { dev.SlotNames[0], dev.SlotNames[1] });
    }
}
