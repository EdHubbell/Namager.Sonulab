using System.IO;
using Sonulab.Core;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;
using Xunit;

public class SlotBlobDisconnectAttributionTests
{
    /// <summary>Answers the name-table list read, then dies on the Nth send thereafter.</summary>
    private sealed class DyingAfterListLink(int dieOnSend) : ISonuLink
    {
        public int Sends;
        public bool IsOpen { get; private set; } = true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() => IsOpen = false;

        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (++Sends >= dieOnSend)
            {
                IsOpen = false;
                throw new DeviceDisconnectedException("USB", new IOException("cable pulled"));
            }
            if (command.StartsWith("read root\\"))
                return Task.FromResult("root\\amp:{\"value\":[\"\",\"\",\"\"]}\r\n");
            // Unreachable in these tests (every case dies on send 1 or 2, before any ACK is read).
            return Task.FromResult("");
        }
    }

    static SlotBlobService Service(ISonuLink link, SlotBlobKind kind) =>
        new(new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0),
            kind, Path.Combine(Path.GetTempPath(), "namager-test-backups"),
            msg => new InvalidOperationException(msg), paceMs: 0, settleMs: 0);

    [Fact] public async Task Upload_interrupted_mid_slot_names_the_amp_slot()
    {
        // Send 1 = the name-table list read; send 2 = the first dwrite. Dying on 2 puts the drop
        // squarely inside the write burst.
        var svc = Service(new DyingAfterListLink(dieOnSend: 2), SlotBlobKind.Amp);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(12, new byte[12288], "Test Amp"));

        Assert.Equal(
            "Device disconnected (USB). Amp slot 12 may be partially written — verify it after reconnecting.",
            ex.Message);
        Assert.Equal("Amp", ex.SlotNoun);
        Assert.Equal(12, ex.SlotIndex);
        Assert.True(ex.WasWriting);
    }

    [Fact] public async Task Upload_interrupted_names_the_ir_slot_with_the_IR_noun()
    {
        var svc = Service(new DyingAfterListLink(dieOnSend: 2), SlotBlobKind.Ir);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(3, new byte[4096], "Test IR"));

        Assert.Contains("IR slot 3 may be partially written", ex.Message);
    }

    [Fact] public async Task A_read_is_not_reported_as_a_half_write()
    {
        // Dying on send 1 kills the pre-write name-table READ. Nothing was written, so the message
        // must stay bare rather than accusing a slot that is fine.
        var svc = Service(new DyingAfterListLink(dieOnSend: 1), SlotBlobKind.Amp);

        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(
            () => svc.UploadAsync(12, new byte[12288], "Test Amp"));

        Assert.Equal("Device disconnected (USB).", ex.Message);
        Assert.False(ex.WasWriting);
    }

    [Fact] public async Task Read_paths_stay_bare()
    {
        var svc = Service(new DyingAfterListLink(dieOnSend: 1), SlotBlobKind.Amp);
        var ex = await Assert.ThrowsAsync<DeviceDisconnectedException>(() => svc.ListAsync());
        Assert.Equal("Device disconnected (USB).", ex.Message);
    }
}
