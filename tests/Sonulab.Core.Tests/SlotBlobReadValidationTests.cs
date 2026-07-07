using Sonulab.Core;
using Sonulab.Core.Services;

/// <summary>A dropped/garbled dread chunk must fail LOUDLY, never silently yield a short
/// blob (slot-26 incident, 2026-07-06: a bad read cached as "no metadata" led a later save
/// to wipe the on-device SSMD block).</summary>
public class SlotBlobReadValidationTests : IDisposable
{
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"blob-val-backups-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, true); }

    /// <summary>Fake that returns an empty response for one dread chunk (simulates a
    /// timeout / desynced serial window).</summary>
    private sealed class DropChunkAmpDevice : FakeAmpDevice
    {
        public int DropChunk { get; set; } = 40;
        public bool Dropping { get; set; } = true;
        public override Task<string> SendAsync(string command, CancellationToken ct = default) =>
            Dropping && command.StartsWith("dread") && command.Contains($"\"chunk\":{DropChunk}}}")
                ? Task.FromResult("")
                : base.SendAsync(command, ct);
    }

    private static DropChunkAmpDevice MakeDevice()
    {
        var dev = new DropChunkAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        return dev;
    }

    [Fact]
    public async Task ReadAmp_throws_on_dropped_chunk_instead_of_returning_short_blob()
    {
        var dev = MakeDevice();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var ex = await Assert.ThrowsAsync<AmpServiceException>(() => svc.ReadAmpAsync(0));
        Assert.Contains("12288", ex.Message);
        Assert.Contains("12160", ex.Message);               // 96 chunks minus the dropped one
    }

    [Fact]
    public async Task Upload_to_occupied_slot_aborts_before_writing_when_backup_read_is_short()
    {
        var dev = MakeDevice();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var payload = Enumerable.Repeat((byte)9, 12288).ToArray();
        await Assert.ThrowsAsync<AmpServiceException>(() => svc.UploadAmpAsync(0, payload, "Clean"));
        Assert.Equal((byte)1, dev.SlotBlobs[0]![0]);        // slot content untouched
        Assert.Equal("Clean", dev.SlotNames[0]);
        Assert.Empty(Directory.Exists(_backupDir) ? Directory.GetFiles(_backupDir) : Array.Empty<string>());
        // no truncated backup file was written to disk
    }

    [Fact]
    public async Task Read_succeeds_when_no_chunk_is_dropped()
    {
        var dev = MakeDevice();
        dev.Dropping = false;
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var blob = await svc.ReadAmpAsync(0);
        Assert.Equal(12288, blob.Length);
    }

    /// <summary>Fake that returns an ODD-LENGTH (torn) hex value for one dread chunk.</summary>
    private sealed class TornHexAmpDevice : FakeAmpDevice
    {
        public int TornChunk { get; set; } = 40;
        public override async Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            var raw = await base.SendAsync(command, ct);
            if (command.StartsWith("dread") && command.Contains($"\"chunk\":{TornChunk}}}"))
                raw = System.Text.RegularExpressions.Regex.Replace(
                    raw, "\"value\":\"[0-9a-fA-F]*\"", "\"value\":\"" + new string('a', 255) + "\"");
            return raw;
        }
    }

    [Fact]
    public async Task Torn_odd_length_hex_fails_loudly_not_with_FormatException()
    {
        var dev = new TornHexAmpDevice();
        dev.SeedAmp(0, "Clean", Enumerable.Repeat((byte)1, 12288).ToArray());
        await dev.OpenAsync();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var ex = await Assert.ThrowsAsync<AmpServiceException>(() => svc.ReadAmpAsync(0));
        Assert.Contains("12288", ex.Message);                // surfaced as a short-read protocol error
    }

    /// <summary>Fake whose reads go short only AFTER a commit has landed — simulates a link
    /// glitch during the post-write verify readback.</summary>
    private sealed class ShortVerifyOnceAmpDevice : FakeAmpDevice
    {
        public int DropChunk { get; set; } = 40;
        private bool _dropped;
        public override Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (!_dropped && CommitSeen && command.StartsWith("dread") && command.Contains($"\"chunk\":{DropChunk}}}"))
            { _dropped = true; return Task.FromResult(""); }
            return base.SendAsync(command, ct);
        }
    }

    [Fact]
    public async Task Short_verify_readback_is_retried_not_treated_as_corruption()
    {
        var dev = new ShortVerifyOnceAmpDevice();
        await dev.OpenAsync();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var payload = Enumerable.Repeat((byte)9, 12288).ToArray();
        await svc.UploadAmpAsync(0, payload, "NewAmp");      // empty target: no backup read
        Assert.Equal("NewAmp", dev.SlotNames[0]);            // upload SUCCEEDED after the retry
        Assert.Equal((byte)9, dev.SlotBlobs[0]![0]);          // slot NOT cleared by the glitched verify
    }
}
