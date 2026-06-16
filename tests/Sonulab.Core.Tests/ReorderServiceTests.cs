using System;
using System.Linq;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class ReorderServiceTests
{
    static (DeviceRepository repo, FakePresetDevice dev) Seed()
    {
        var dev = new FakePresetDevice();
        // 4 presets in slots 0..3, content tagged so we can tell them apart
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        return (new DeviceRepository(new SonuClient(dev)), dev);
    }

    static async Task<string[]> Names(DeviceRepository repo) =>
        (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();

    [Fact] public async Task Move_down_reorders_names_in_order()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 1, to: 3);
        var names = await Names(repo);
        Assert.Equal("A", names[0]);
        Assert.Equal("C", names[1]);
        Assert.Equal("D", names[2]);
        Assert.Equal("B", names[3]);     // B moved from slot 1 to slot 3
    }

    [Fact] public async Task Move_carries_content_with_the_preset()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 1, to: 3);
        var slot3 = await repo.ReadPresetAsync(3);
        Assert.Equal("\"mB\"", slot3.GetValueJson(@"root\app\amp\amp"));   // B's content followed B
        var slot1 = await repo.ReadPresetAsync(1);
        Assert.Equal("\"mC\"", slot1.GetValueJson(@"root\app\amp\amp"));   // C shifted up into slot 1
    }

    [Fact] public async Task Move_up_reorders_correctly()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(from: 3, to: 0);
        Assert.Equal(new[] { "D", "A", "B", "C" }, (await Names(repo))[..4]);
    }

    [Fact] public async Task Same_index_move_is_noop()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await new ReorderService(repo).MoveAsync(2, 2);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(repo))[..4]);
    }

    [Fact] public async Task Reports_progress()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        var seen = new List<ReorderProgress>();
        await new ReorderService(repo).MoveAsync(1, 3, new Progress<ReorderProgress>(p => { lock (seen) seen.Add(p); }));
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.True(p.Done <= p.Total));
    }

    // Fails exactly once on the Nth save; tracks that it fired and how many saves ran (so we can prove rollback saves executed).
    sealed class FailOnceOnSave : FakePresetDevice
    {
        private readonly int _failOnSave; private int _saves; public bool Fired;
        public int Saves => _saves;
        public FailOnceOnSave(int failOnSave) => _failOnSave = failOnSave;
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\""))
            {
                _saves++;
                if (!Fired && _saves == _failOnSave) { Fired = true; throw new System.IO.IOException("simulated save failure"); }
            }
            return base.SendAsync(command, ct);
        }
    }

    // Fails exactly once on a finalize rename (a dwrite chunk:-1 whose decoded name is NOT a temp "__sstmp_" name).
    sealed class FailOnceOnFinalRename : FakePresetDevice
    {
        public bool Fired;
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (!Fired && command.StartsWith("dwrite root\\presets:", StringComparison.Ordinal) && command.Contains("\"chunk\":-1"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(command, "\"value\":\"([0-9a-fA-F]*)\"");
                if (m.Success)
                {
                    var hex = m.Groups[1].Value;
                    var bytes = new byte[hex.Length / 2];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
                    var name = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
                    if (name.Length > 0 && !name.StartsWith("__sstmp_", StringComparison.Ordinal))
                    { Fired = true; throw new System.IO.IOException("simulated rename failure"); }
                }
            }
            return base.SendAsync(command, ct);
        }
    }

    static void SeedABCD(FakePresetDevice dev)
    {
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        dev.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
    }

    [Fact] public async Task Rollback_restores_original_on_save_failure()
    {
        var dev = new FailOnceOnSave(failOnSave: 2);   // fail the 2nd save (mid forward pass)
        SeedABCD(dev); await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(repo).MoveAsync(1, 3));

        Assert.True(dev.Fired, "the injected save failure should have fired");
        Assert.True(dev.Saves > 2, "rollback saves must have run after the failure");
        var slots = await repo.ListPresetsAsync();
        Assert.Equal(new[] { "A", "B", "C", "D" }, slots.Take(4).Select(s => s.Name).ToArray());
        Assert.Equal("\"mB\"", (await repo.ReadPresetAsync(1)).GetValueJson(@"root\app\amp\amp"));
    }

    [Fact] public async Task Rollback_restores_original_on_phase2_rename_failure()
    {
        var dev = new FailOnceOnFinalRename();
        SeedABCD(dev); await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(repo).MoveAsync(1, 3));

        Assert.True(dev.Fired, "the injected rename failure should have fired");
        var slots = await repo.ListPresetsAsync();
        Assert.Equal(new[] { "A", "B", "C", "D" }, slots.Take(4).Select(s => s.Name).ToArray());
        Assert.Equal("\"mC\"", (await repo.ReadPresetAsync(2)).GetValueJson(@"root\app\amp\amp"));
    }

    [Fact] public async Task Move_through_empty_slot_in_range_deletes_and_shifts()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        // slot 1 intentionally empty
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));

        await new ReorderService(repo).MoveAsync(0, 2);   // occupants [0,-1,2] -> [-1,2,0]

        var names = (await repo.ListPresetsAsync()).Select(s => s.Name).ToArray();
        Assert.Equal("", names[0]);
        Assert.Equal("C", names[1]);
        Assert.Equal("A", names[2]);
        Assert.Equal("\"mA\"", (await repo.ReadPresetAsync(2)).GetValueJson(@"root\app\amp\amp"));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 30)]
    [InlineData(31, 1)]
    public async Task Out_of_range_indices_throw(int from, int to)
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ReorderService(repo).MoveAsync(from, to));
    }

    [Fact] public async Task Moving_an_empty_slot_throws()
    {
        var (repo, dev) = Seed(); await dev.OpenAsync();   // slots 4..29 are empty
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReorderService(repo).MoveAsync(10, 0));
    }

    [Fact] public async Task Refuses_reorder_if_a_preset_uses_the_reserved_temp_prefix()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "__sstmp_9", new[] { @"root\app\amp\amp:{""value"":""mB""}" });  // squats the reserved prefix
        dev.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReorderService(repo).MoveAsync(0, 2));
    }
}
