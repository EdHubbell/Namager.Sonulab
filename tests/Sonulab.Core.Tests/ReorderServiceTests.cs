using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class ReorderServiceTests
{
    static FakePresetDevice Dev(int used = 4)
    {
        var d = new FakePresetDevice();
        for (int i = 0; i < used; i++)
        {
            var nm = "P" + i;
            var tag = "m" + i;
            d.SeedSlot(i, nm, new[] { $@"root\app\amp\amp:{{""value"":""{tag}""}}" });
        }
        // Seed first 4 as A,B,C,D for tests that use them by name
        if (used >= 1) d.SeedSlot(0, "A", new[] { $@"root\app\amp\amp:{{""value"":""mA""}}" });
        if (used >= 2) d.SeedSlot(1, "B", new[] { $@"root\app\amp\amp:{{""value"":""mB""}}" });
        if (used >= 3) d.SeedSlot(2, "C", new[] { $@"root\app\amp\amp:{{""value"":""mC""}}" });
        if (used >= 4) d.SeedSlot(3, "D", new[] { $@"root\app\amp\amp:{{""value"":""mD""}}" });
        return d;
    }
    static DeviceRepository Repo(FakePresetDevice d) => new(new SonuClient(d));
    static async Task<string[]> Names(DeviceRepository r) => (await r.ListPresetsAsync()).Select(s => s.Name).ToArray();
    static async Task<string?> Amp(DeviceRepository r, int i) => (await r.ReadPresetAsync(i)).GetValueJson(@"root\app\amp\amp");

    [Fact] public async Task Move_up_rotates_order_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(from: 3, to: 1);   // D up to slot 1
        Assert.Equal(new[] { "A", "D", "B", "C" }, (await Names(r))[..4]);
        Assert.Equal("\"mD\"", await Amp(r, 1));   // content followed the name
        Assert.Equal("\"mB\"", await Amp(r, 2));
        Assert.Equal("\"mC\"", await Amp(r, 3));
    }

    [Fact] public async Task Move_down_rotates_order_and_content()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(from: 0, to: 2);   // A down to slot 2
        Assert.Equal(new[] { "B", "C", "A", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mA\"", await Amp(r, 2));
        Assert.Equal("\"mB\"", await Amp(r, 0));
    }

    [Fact] public async Task Same_index_is_noop()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(2, 2);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
    }

    [Fact] public async Task Fallback_when_no_empty_temp_slot_still_reorders()
    {
        var d = Dev(used: 30); await d.OpenAsync(); var r = Repo(d);  // full device -> no temp slot
        await new ReorderService(r).MoveAsync(2, 0);
        var names = await Names(r);
        Assert.Equal("C", names[0]); Assert.Equal("A", names[1]); Assert.Equal("B", names[2]);
    }

    [Theory]
    [InlineData(-1, 2)] [InlineData(0, 30)] [InlineData(31, 1)]
    public async Task Out_of_range_throws(int from, int to)
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ReorderService(r).MoveAsync(from, to));
    }

    [Fact] public async Task Moving_empty_slot_throws()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);   // slots 4..29 empty
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReorderService(r).MoveAsync(10, 0));
    }

    sealed class FailOnceOnSave : FakePresetDevice
    {
        private readonly int _n; private int _saves; public bool Fired;
        public FailOnceOnSave(int n) => _n = n;
        public override Task<string> SendAsync(string command, System.Threading.CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\"")) { _saves++; if (!Fired && _saves == _n) { Fired = true; throw new System.IO.IOException("fail"); } }
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Rollback_restores_original_on_save_failure()
    {
        var d = new FailOnceOnSave(2);
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        d.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        d.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(r).MoveAsync(3, 0));
        Assert.True(d.Fired);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mB\"", await Amp(r, 1));
    }

    [Fact] public async Task Reports_progress()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        var seen = new List<ReorderProgress>();
        await new ReorderService(r).MoveAsync(3, 1, new Progress<ReorderProgress>(p => { lock (seen) seen.Add(p); }));
        Assert.NotEmpty(seen);
    }

    [Fact] public async Task Move_across_empty_interior_slot_reorders_without_data_loss()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        // slot 1 intentionally empty
        d.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        d.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveAsync(3, 0);   // range [0,3] contains the empty slot -> fallback path
        var names = await Names(r);
        Assert.Equal(new[] { "D", "A", "", "C" }, names[..4]);
        Assert.Equal("\"mD\"", await Amp(r, 0));
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal("\"mC\"", await Amp(r, 3));
    }

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
                    var nm = System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0');
                    if (nm.Length > 0 && !nm.StartsWith("__sstmp_", StringComparison.Ordinal)) { Fired = true; throw new System.IO.IOException("rename fail"); }
                }
            }
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task Rollback_restores_original_on_finalize_rename_failure()
    {
        var d = new FailOnceOnFinalRename();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        d.SeedSlot(2, "C", new[] { @"root\app\amp\amp:{""value"":""mC""}" });
        d.SeedSlot(3, "D", new[] { @"root\app\amp\amp:{""value"":""mD""}" });
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(() => new ReorderService(r).MoveAsync(3, 0));
        Assert.True(d.Fired);
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mD\"", await Amp(r, 3));
    }

    [Fact] public async Task MoveStep_down_swaps_with_occupied_neighbour()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: false);   // A <-> B
        Assert.Equal(new[] { "B", "A", "C", "D" }, (await Names(r))[..4]);
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal("\"mB\"", await Amp(r, 0));
    }

    [Fact] public async Task MoveStep_up_swaps_with_occupied_neighbour()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 3, up: true);    // D <-> C
        Assert.Equal(new[] { "A", "B", "D", "C" }, (await Names(r))[..4]);
        Assert.Equal("\"mD\"", await Amp(r, 2));
    }

    [Fact] public async Task MoveStep_up_from_first_slot_is_noop()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: true);    // to = -1
        Assert.Equal(new[] { "A", "B", "C", "D" }, (await Names(r))[..4]);
    }

    [Fact] public async Task MoveStep_down_from_last_slot_is_noop()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);
        d.SeedSlot(29, "Z", new[] { @"root\app\amp\amp:{""value"":""mZ""}" });
        await new ReorderService(r).MoveStepAsync(from: 29, up: false);  // to = 30
        Assert.Equal("Z", (await Names(r))[29]);
    }

    [Fact] public async Task MoveStep_on_empty_slot_throws()
    {
        var d = Dev(); await d.OpenAsync(); var r = Repo(d);            // slots 4..29 empty
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ReorderService(r).MoveStepAsync(from: 10, up: true));
    }

    sealed class CountingDevice : FakePresetDevice
    {
        public int Saves, Selects, ParamWrites, Dreads;
        public override Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.Contains("\"save\":\"save\"")) Saves++;
            else if (command.StartsWith("write root\\app\\preset:", StringComparison.Ordinal)) Selects++;
            else if (command.StartsWith("write root\\app\\", StringComparison.Ordinal)) ParamWrites++;
            else if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task MoveStep_down_into_empty_relocates_with_one_copy()
    {
        var d = new CountingDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: false);          // A relocates to slot 1
        var names = await Names(r);
        Assert.Equal("", names[0]);
        Assert.Equal("A", names[1]);
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal(1, d.Saves);            // ONE copy, not a 3-copy swap
        Assert.Equal(0, d.ParamWrites);      // proves it did NOT use the slow param-replay fallback
        Assert.True(d.Selects >= 1);
    }

    [Fact] public async Task MoveStep_up_into_empty_relocates()
    {
        var d = new FakePresetDevice();
        d.SeedSlot(1, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 0 empty
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 1, up: true);           // A relocates to slot 0
        var names = await Names(r);
        Assert.Equal("A", names[0]);
        Assert.Equal("", names[1]);
        Assert.Equal("\"mA\"", await Amp(r, 0));
    }

    [Fact] public async Task MoveStep_relocate_rolls_back_on_save_failure()
    {
        var d = new FailOnceOnSave(1);                                         // fail the relocate's save
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(
            () => new ReorderService(r).MoveStepAsync(from: 0, up: false));
        Assert.True(d.Fired);
        var names = await Names(r);
        Assert.Equal("A", names[0]);          // original restored
        Assert.Equal("", names[1]);           // destination left empty
        Assert.Equal("\"mA\"", await Amp(r, 0));
    }

    [Fact] public async Task MoveStep_relocate_rolls_back_on_final_rename_failure()
    {
        var d = new FailOnceOnFinalRename();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(
            () => new ReorderService(r).MoveStepAsync(from: 0, up: false));     // fails at final rename, AFTER delete(from)
        Assert.True(d.Fired);
        var names = await Names(r);
        Assert.Equal("A", names[0]);          // source rebuilt by rollback
        Assert.Equal("", names[1]);           // destination cleared
        Assert.Equal("\"mA\"", await Amp(r, 0));   // content restored, not just the name
    }

    // ---- Lean (read-free) fast paths: the perf fix that drops the 8 KB content backup ----

    [Fact] public async Task MoveStep_swap_reads_no_preset_content()
    {
        var d = new CountingDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });   // slot 2.. empty -> temp available
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: false);          // swap A<->B
        int dreads = d.Dreads, saves = d.Saves;   // snapshot BEFORE the Amp() content reads below
        Assert.Equal(new[] { "B", "A" }, (await Names(r))[..2]);
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal("\"mB\"", await Amp(r, 0));
        Assert.Equal(0, dreads);     // lean swap pulls NO 8 KB content to the host
        Assert.Equal(3, saves);      // exactly the 3 select+save copies
    }

    [Fact] public async Task MoveStep_relocate_reads_no_preset_content()
    {
        var d = new CountingDevice();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 1 empty
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: false);          // relocate into the gap
        Assert.Equal(new[] { "", "A" }, (await Names(r))[..2]);
        Assert.Equal(0, d.Dreads);
        Assert.Equal(1, d.Saves);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task MoveStep_swap_rolls_back_to_original_on_save_failure(int failOnSave)
    {
        var d = new FailOnceOnSave(failOnSave);
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(
            () => new ReorderService(r).MoveStepAsync(from: 0, up: false));
        Assert.True(d.Fired);
        var names = await Names(r);
        Assert.Equal("A", names[0]); Assert.Equal("B", names[1]);   // original layout restored
        Assert.Equal("\"mA\"", await Amp(r, 0));
        Assert.Equal("\"mB\"", await Amp(r, 1));
        Assert.DoesNotContain(names, n => n.StartsWith("__sstmp_", StringComparison.Ordinal));   // no temp slots left
    }

    [Fact] public async Task MoveStep_swap_completes_despite_transient_final_rename_failure()
    {
        // The final rename happens AFTER the content is swapped (past the point of no return),
        // so recovery finishes the swap rather than undoing it.
        var d = new FailOnceOnFinalRename();
        d.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        d.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await d.OpenAsync(); var r = Repo(d);
        await new ReorderService(r).MoveStepAsync(from: 0, up: false);   // no throw: swap finishes
        Assert.True(d.Fired);
        var names = await Names(r);
        Assert.Equal("B", names[0]); Assert.Equal("A", names[1]);
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal("\"mB\"", await Amp(r, 0));
        Assert.DoesNotContain(names, n => n.StartsWith("__sstmp_", StringComparison.Ordinal));
    }

    // Throws on the Nth `read root\presets` (used to fail the post-move name verify).
    sealed class FailOnceOnListRead : FakePresetDevice
    {
        private readonly int _n; private int _count; public bool Fired;
        public FailOnceOnListRead(int n) => _n = n;
        public override Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command == "read root\\presets") { _count++; if (!Fired && _count == _n) { Fired = true; throw new System.IO.IOException("list read fail"); } }
            return base.SendAsync(command, ct);
        }
    }

    [Fact] public async Task MoveStep_relocate_up_rolls_back_safely_when_verify_fails()
    {
        // Regression: an UPWARD relocate whose post-move verify fails must NOT lose content.
        // (read #1 = MoveStep's initial list; read #2 = the verify, which we fail — by then the
        // move has fully applied, so the surviving copy sits in `to`, the LOWER-indexed slot.)
        var d = new FailOnceOnListRead(2);
        d.SeedSlot(1, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });   // slot 0 empty
        await d.OpenAsync(); var r = Repo(d);
        await Assert.ThrowsAnyAsync<System.Exception>(
            () => new ReorderService(r).MoveStepAsync(from: 1, up: true));      // relocate UP into slot 0
        Assert.True(d.Fired);
        var names = await Names(r);
        Assert.Equal("\"mA\"", await Amp(r, 1));   // content preserved (the bug destroyed it)
        Assert.Equal("A", names[1]);               // restored to the original slot
        Assert.Equal("", names[0]);
        Assert.DoesNotContain(names, n => n.StartsWith("__sstmp_", StringComparison.Ordinal));
    }

    [Fact] public async Task MoveStep_swap_falls_back_and_still_swaps_when_device_full()
    {
        var d = Dev(used: 30); await d.OpenAsync(); var r = Repo(d);   // no empty temp slot anywhere
        await new ReorderService(r).MoveStepAsync(from: 0, up: false); // swaps via the replay fallback
        var names = await Names(r);
        Assert.Equal("B", names[0]); Assert.Equal("A", names[1]);
        Assert.Equal("\"mA\"", await Amp(r, 1));
        Assert.Equal("\"mB\"", await Amp(r, 0));
    }
}
