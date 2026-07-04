using System.Text;

public class FakeAmpDeviceTests
{
    private static byte[] NamePad(string name)
    {
        var b = new byte[128];
        Encoding.ASCII.GetBytes(name).CopyTo(b, 0);
        return b;
    }

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    [Fact]
    public async Task Read_amp_list_returns_30_names()
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(2, "Crunchy", new byte[12288]);
        await dev.OpenAsync();
        var raw = await dev.SendAsync(@"read root\amp");
        Assert.Contains("\"Crunchy\"", raw);
        // 30 slots -> 29 commas between quoted names
        Assert.Equal(29, raw.Count(c => c == ','));
    }

    [Fact]
    public async Task Upload_sequence_commits_on_name_at_minus1()
    {
        var dev = new FakeAmpDevice();
        await dev.OpenAsync();
        var payload = new byte[12288];
        payload[0] = 0xAB; payload[12287] = 0xCD;

        var ack0 = await dev.SendAsync($@"dwrite root\amp:{{""index"":5,""chunk"":0,""value"":""{Hex(NamePad("New Amp"))}""}}");
        Assert.Contains("\"chunk\":1}", ack0);
        for (int chk = 1; chk <= 96; chk++)
        {
            var seg = payload.Skip((chk - 1) * 128).Take(128).ToArray();
            var ack = await dev.SendAsync($@"dwrite root\amp:{{""index"":5,""chunk"":{chk},""value"":""{Hex(seg)}""}}");
            Assert.Contains($"\"chunk\":{(chk < 96 ? chk + 1 : -1)}}}", ack);
        }
        Assert.Null(dev.SlotNames[5]);                     // not committed yet
        var ackC = await dev.SendAsync($@"dwrite root\amp:{{""index"":5,""chunk"":-1,""value"":""{Hex(NamePad("New Amp"))}""}}");
        Assert.Contains("\"chunk\":-1}", ackC);
        Assert.Equal("New Amp", dev.SlotNames[5]);
        Assert.Equal(payload, dev.SlotBlobs[5]);
        Assert.True(dev.CommitSeen);
    }

    [Fact]
    public async Task Zeros_at_minus1_deletes_and_discards_staged()
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(3, "Old", Enumerable.Repeat((byte)0x11, 12288).ToArray());
        await dev.OpenAsync();
        await dev.SendAsync($@"dwrite root\amp:{{""index"":3,""chunk"":0,""value"":""{Hex(NamePad("Won't land"))}""}}");
        await dev.SendAsync($@"dwrite root\amp:{{""index"":3,""chunk"":-1,""value"":""{Hex(new byte[128])}""}}");
        Assert.Null(dev.SlotNames[3]);                     // deleted
        Assert.Null(dev.SlotBlobs[3]);
    }

    [Fact]
    public async Task Rename_via_minus1_without_staged_payload_keeps_blob()
    {
        var blob = Enumerable.Repeat((byte)0x22, 12288).ToArray();
        var dev = new FakeAmpDevice();
        dev.SeedAmp(1, "Before", blob);
        await dev.OpenAsync();
        await dev.SendAsync($@"dwrite root\amp:{{""index"":1,""chunk"":-1,""value"":""{Hex(NamePad("After"))}""}}");
        Assert.Equal("After", dev.SlotNames[1]);
        Assert.Equal(blob, dev.SlotBlobs[1]);              // content untouched
    }

    [Fact]
    public async Task Dread_returns_seeded_blob_chunks_and_nothing_for_empty()
    {
        var blob = new byte[12288]; blob[128] = 0x99;      // first byte of chunk 2
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "X", blob);
        await dev.OpenAsync();
        var r2 = await dev.SendAsync(@"dread root\amp:{""index"":0,""chunk"":2}");
        Assert.Contains("\"value\":\"99", r2);
        var rEmpty = await dev.SendAsync(@"dread root\amp:{""index"":7,""chunk"":1}");
        Assert.Equal("", rEmpty);                          // empty slot: no record (real device times out)
        Assert.Contains(dev.CommandLog, c => c.Contains("\"index\":7"));
    }

    [Fact]
    public async Task Corrupt_ack_reports_wrong_next_chunk()
    {
        var dev = new FakeAmpDevice { CorruptAckAtChunk = 4 };
        await dev.OpenAsync();
        await dev.SendAsync($@"dwrite root\amp:{{""index"":0,""chunk"":0,""value"":""{Hex(NamePad("A"))}""}}");
        var ack = await dev.SendAsync($@"dwrite root\amp:{{""index"":0,""chunk"":4,""value"":""{Hex(new byte[128])}""}}");
        Assert.DoesNotContain("\"chunk\":5}", ack);
    }
}
