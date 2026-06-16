using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class DuplicateTests
{
    [Fact] public async Task Duplicate_copies_source_content_to_dest_with_new_name()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(2, "Original", new[] { @"root\app\amp\amp:{""value"":""AmpX""}", @"root\app\amp\vol:{""value"":42.0}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        await repo.DuplicateAsync(sourceIndex: 2, destIndex: 11, newName: "Original copy");

        var slots = await repo.ListPresetsAsync();
        Assert.Equal("Original copy", slots[11].Name);
        var src = await repo.ReadPresetAsync(2);
        var dst = await repo.ReadPresetAsync(11);
        Assert.Equal(src.ToBytes(), dst.ToBytes());
    }
}
