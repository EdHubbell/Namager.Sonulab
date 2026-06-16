using System.Text;
using Sonulab.Core;
using Xunit;

public class FakePresetDeviceTests
{
    static FakePresetDevice Dev()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(0, "Alpha", new[] { @"root\app\amp\amp:{""value"":""AmpA""}", @"root\app\amp\vol:{""value"":50.0}" });
        d.SeedSlot(1, "Beta",  new[] { @"root\app\amp\amp:{""value"":""AmpB""}", @"root\app\amp\vol:{""value"":60.0}" });
        return d;
    }

    [Fact] public async Task Read_presets_returns_30_names()
    {
        var d = Dev(); await d.OpenAsync();
        var c = new SonuClient(d);
        var names = await c.ReadListAsync(@"root\presets");
        Assert.Equal(30, names.Count);
        Assert.Equal("Alpha", names[0]);
        Assert.Equal("", names[2]);
    }

    [Fact] public async Task Select_then_save_to_other_named_slot_copies_content()
    {
        var d = Dev(); await d.OpenAsync(); var c = new SonuClient(d);
        // name slot 5 "Gamma", select Alpha (loads its live state), save as Gamma
        var nameBytes = new byte[128]; Encoding.ASCII.GetBytes("Gamma").CopyTo(nameBytes, 0);
        await c.DWriteChunkAsync(@"root\presets", 5, -1, nameBytes);
        await c.WriteAsync(@"root\app\preset", "\"Alpha\"");          // select (no save)
        await c.SaveAsync(@"root\app\preset", "Gamma");               // save live -> slot named Gamma (5)
        var blob5 = await c.DReadBlobAsync(@"root\presets", 5, 64);
        var blob0 = await c.DReadBlobAsync(@"root\presets", 0, 64);
        Assert.Equal(blob0, blob5);                                   // slot 5 now holds Alpha's content
    }

    [Fact] public async Task Content_dwrite_is_ignored_but_name_dwrite_works()
    {
        var d = Dev(); await d.OpenAsync(); var c = new SonuClient(d);
        await c.DWriteChunkAsync(@"root\presets", 0, 1, new byte[128]); // content write -> NO-OP
        var blob = await c.DReadBlobAsync(@"root\presets", 0, 64);
        Assert.Contains("AmpA", Encoding.ASCII.GetString(blob));        // content unchanged
        var zero = new byte[128];
        await c.DWriteChunkAsync(@"root\presets", 0, -1, zero);         // name -> empty = delete
        Assert.Equal("", (await c.ReadListAsync(@"root\presets"))[0]);
    }
}
