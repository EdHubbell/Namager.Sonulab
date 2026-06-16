using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Xunit;

public class WritePresetToSlotTests
{
    [Fact] public async Task Writes_document_to_empty_slot_and_verifies()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Source", new[] { @"root\app\amp\amp:{""value"":""AmpA""}", @"root\app\amp\vol:{""value"":50.0}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        var doc = await repo.ReadPresetAsync(0);                  // read Source's content
        await repo.WritePresetToSlotAsync(7, "Copy", doc);        // write it to empty slot 7 named "Copy"

        var slots = await repo.ListPresetsAsync();
        Assert.Equal("Copy", slots[7].Name);
        Assert.Equal(doc.ToBytes(), (await repo.ReadPresetAsync(7)).ToBytes());
    }

    [Fact] public async Task Verify_failure_throws()
    {
        // A device that drops the save (content never lands) must trip the read-back verify.
        var dev = new DropSaveDevice();
        dev.SeedSlot(0, "Source", new[] { @"root\app\amp\amp:{""value"":""AmpA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var doc = await repo.ReadPresetAsync(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.WritePresetToSlotAsync(7, "Copy", doc));
    }

    // FakePresetDevice variant whose SaveRx handler does nothing (simulates a failed write).
    sealed class DropSaveDevice : FakePresetDevice
    {
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
            => command.Contains("\"save\":\"save\"") ? Task.FromResult("") : base.SendAsync(command, ct);
    }
}
