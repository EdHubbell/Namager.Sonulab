using System.Text;

public class FakeSlotBlobDeviceTests
{
    private static byte[] NamePad(string name)
    { var b = new byte[128]; Encoding.ASCII.GetBytes(name).CopyTo(b, 0); return b; }
    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Fact]
    public async Task Ir_device_uses_ir_path_and_32_chunks()
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(1, "Cab", new byte[4096]);
        await dev.OpenAsync();
        var raw = await dev.SendAsync(@"read root\ir");
        Assert.Contains("\"Cab\"", raw);
        // last payload chunk (32) ACKs -1
        await dev.SendAsync($@"dwrite root\ir:{{""index"":0,""chunk"":0,""value"":""{Hex(NamePad("X"))}""}}");
        string ack = "";
        for (int chk = 1; chk <= 32; chk++)
            ack = await dev.SendAsync($@"dwrite root\ir:{{""index"":0,""chunk"":{chk},""value"":""{Hex(new byte[128])}""}}");
        Assert.Contains("\"chunk\":-1}", ack);
    }

    [Fact]
    public async Task Out_of_order_payload_chunk_is_rejected_with_true_next_expected()
    {
        var dev = new FakeIrDevice();
        await dev.OpenAsync();
        await dev.SendAsync($@"dwrite root\ir:{{""index"":2,""chunk"":0,""value"":""{Hex(NamePad("A"))}""}}");
        await dev.SendAsync($@"dwrite root\ir:{{""index"":2,""chunk"":1,""value"":""{Hex(new byte[128])}""}}");
        // skip chunk 2, write chunk 3 -> ACK must say the TRUE next expected (2), and chunk 3 must not stage
        var ack = await dev.SendAsync($@"dwrite root\ir:{{""index"":2,""chunk"":3,""value"":""{Hex(Enumerable.Repeat((byte)0xEE, 128).ToArray())}""}}");
        Assert.Contains("\"chunk\":2}", ack);
        // finish in order and commit; the skipped-then-rejected data must not appear
        for (int chk = 2; chk <= 32; chk++)
            await dev.SendAsync($@"dwrite root\ir:{{""index"":2,""chunk"":{chk},""value"":""{Hex(new byte[128])}""}}");
        await dev.SendAsync($@"dwrite root\ir:{{""index"":2,""chunk"":-1,""value"":""{Hex(NamePad("A"))}""}}");
        Assert.All(dev.SlotBlobs[2]!, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Minus1_is_accepted_any_time_for_rename_and_delete()
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(4, "Old", Enumerable.Repeat((byte)9, 4096).ToArray());
        await dev.OpenAsync();
        var ack = await dev.SendAsync($@"dwrite root\ir:{{""index"":4,""chunk"":-1,""value"":""{Hex(NamePad("New"))}""}}");
        Assert.Contains("\"chunk\":-1}", ack);
        Assert.Equal("New", dev.SlotNames[4]);
        Assert.Equal(Enumerable.Repeat((byte)9, 4096).ToArray(), dev.SlotBlobs[4]);
    }
}
