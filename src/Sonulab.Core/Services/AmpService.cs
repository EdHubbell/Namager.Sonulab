using System.Text;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class AmpServiceException(string message) : Exception(message);

public enum AmpUploadStage { BackingUp, Writing, Verifying, Done }

/// <summary>Progress for the ~3s guarded upload. ChunksTotal is always 98
/// (chunk 0 = name, 1..96 = payload, -1 = commit).</summary>
public sealed record AmpUploadProgress(AmpUploadStage Stage, int ChunksDone, int ChunksTotal);

/// <summary>Guarded amp-slot operations on root\amp. Mirrors DeviceRepository's shape.
/// The write discipline (backup -> ACK-verified chunked write -> name-at-chunk:-1 commit ->
/// read-back verify) is lifted from the hardware-verified tools/HwCheck path; this class is
/// the ONLY implementation of that sequence (HwCheck calls it too).</summary>
public sealed class AmpService
{
    public const int SlotCount = 30;
    public const int AmpChunks = 96;             // 12288 / 128
    public const int AmpBytes = 12288;
    public const int NameMaxChars = 31;          // device name cap
    private const string AmpList = @"root\amp";

    private readonly SonuClient _client;
    private readonly string _backupDir;
    private readonly int _paceMs;                // extra delay between ACKed chunks
    private readonly int _settleMs;              // flash-settle pause before verify readback

    public AmpService(SonuClient client, string backupDir, int paceMs = 25, int settleMs = 750)
    { _client = client; _backupDir = backupDir; _paceMs = paceMs; _settleMs = settleMs; }

    public async Task<IReadOnlyList<AmpSlot>> ListAmpsAsync(CancellationToken ct = default)
    {
        var names = await _client.ReadListAsync(AmpList, ct);
        var slots = new List<AmpSlot>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(new AmpSlot(i, i < names.Count ? names[i] : ""));
        return slots;
    }

    public Task<byte[]> ReadAmpAsync(int index, CancellationToken ct = default) =>
        _client.DReadBlobAsync(AmpList, index, AmpChunks, ct);

    public async Task DeleteAmpAsync(int index, CancellationToken ct = default)
    {
        var names = await _client.ReadListAsync(AmpList, ct);
        if (index < 0 || index >= names.Count || string.IsNullOrEmpty(names[index]))
            return;                                                       // already empty: no-op
        await BackupSlotAsync(index, "-deleted", ct);
        await _client.DWriteChunkAsync(AmpList, index, -1, new byte[128], ct);
    }

    public Task RenameAmpAsync(int index, string name, CancellationToken ct = default)
    {
        if (index is < 0 or >= SlotCount)
            throw new AmpServiceException($"Slot must be 0..{SlotCount - 1}, got {index}.");
        return _client.DWriteChunkAsync(AmpList, index, -1, NamePad(ValidateName(name)), ct);
    }

    /// <summary>Dread the slot and save it under the backup dir. Callers must ensure the
    /// slot is OCCUPIED first — dreading an empty slot is 96 timeouts and is the prime
    /// suspect for killing a following commit (see HwCheck upload notes).</summary>
    private async Task<byte[]> BackupSlotAsync(int index, string suffix, CancellationToken ct)
    {
        Directory.CreateDirectory(_backupDir);
        var blob = await _client.DReadBlobAsync(AmpList, index, AmpChunks, ct);
        var path = Path.Combine(_backupDir, $"amp-{index}-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}.vxamp");
        await File.WriteAllBytesAsync(path, blob, ct);
        return blob;
    }

    /// <summary>Trim + validate an amp name: non-empty, ASCII, truncated to 31 chars.</summary>
    internal static string ValidateName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new AmpServiceException("Amp name must not be empty.");
        if (n.Any(ch => ch > 127)) throw new AmpServiceException("Amp name must be ASCII.");
        return n.Length > NameMaxChars ? n[..NameMaxChars] : n;
    }

    internal static byte[] NamePad(string name)
    {
        var buf = new byte[128];
        var b = Encoding.ASCII.GetBytes(name);
        Array.Copy(b, buf, Math.Min(b.Length, 128));
        return buf;
    }

    /// <summary>Guarded amp upload (the hardware-verified HwCheck sequence):
    /// backup (occupied slots only — NEVER dread an empty slot) -> chunk 0 = name ->
    /// chunks 1..96 = payload -> chunk -1 = the NAME AGAIN (the commit; zeros there would
    /// delete). Every chunk's ACK is checked; abort on mismatch leaves the slot uncommitted.
    /// Read-back verify; on mismatch the slot is CLEARED so a corrupt amp is never selectable.</summary>
    public async Task UploadAmpAsync(int slot, byte[] vxampBytes, string name,
        IProgress<AmpUploadProgress>? progress = null, CancellationToken ct = default)
    {
        if (slot is < 0 or >= SlotCount)
            throw new AmpServiceException($"Slot must be 0..{SlotCount - 1}, got {slot}.");
        if (vxampBytes.Length != AmpBytes)
            throw new AmpServiceException($"Expected a {AmpBytes}-byte .vxamp, got {vxampBytes.Length} B.");
        var cleanName = ValidateName(name);
        var nameBuf = NamePad(cleanName);
        const int totalChunks = 98;                        // name + 96 payload + commit

        // 1. Backup — ONLY if the name table says the slot is occupied. Skipping the dread on
        // empty slots is not an optimization: a 96-chunk dread right before the write burst is
        // the prime suspect for the commit being silently discarded (HwCheck finding).
        var names = await _client.ReadListAsync(AmpList, ct);
        if (slot >= 0 && slot < names.Count && !string.IsNullOrEmpty(names[slot]))
        {
            progress?.Report(new(AmpUploadStage.BackingUp, 0, totalChunks));
            await BackupSlotAsync(slot, "", ct);
        }

        // 2. ACK-verified write burst.
        int done = 0;
        async Task WriteChunkAckedAsync(int chunk, byte[] data, int expectNext)
        {
            var raw = await _client.DWriteChunkAsync(AmpList, slot, chunk, data, ct);
            var m = System.Text.RegularExpressions.Regex.Match(raw, "\"chunk\":(-?\\d+)}");
            if (!m.Success || int.Parse(m.Groups[1].Value) != expectNext)
                throw new AmpServiceException(
                    $"Device ACK missing/mismatched at chunk {chunk}: got '{(m.Success ? m.Groups[1].Value : "none")}', expected {expectNext}. Upload aborted before commit; slot {slot} is unchanged.");
            progress?.Report(new(AmpUploadStage.Writing, ++done, totalChunks));
            if (_paceMs > 0) await Task.Delay(_paceMs, ct);
        }

        await WriteChunkAckedAsync(0, nameBuf, 1);
        var chunk128 = new byte[128];
        for (int chk = 1; chk <= AmpChunks; chk++)
        {
            Array.Copy(vxampBytes, (chk - 1) * 128, chunk128, 0, 128);
            await WriteChunkAckedAsync(chk, chunk128, chk < AmpChunks ? chk + 1 : -1);
        }
        await WriteChunkAckedAsync(-1, nameBuf, -1);       // the commit

        // 3. Read-back verify (after a flash-settle pause).
        progress?.Report(new(AmpUploadStage.Verifying, totalChunks, totalChunks));
        if (_settleMs > 0) await Task.Delay(_settleMs, ct);
        var readBack = await _client.DReadBlobAsync(AmpList, slot, AmpChunks, ct);
        if (!readBack.AsSpan().SequenceEqual(vxampBytes))
        {
            int firstDiff = -1;
            int n = Math.Min(readBack.Length, vxampBytes.Length);
            for (int i = 0; i < n; i++) if (readBack[i] != vxampBytes[i]) { firstDiff = i; break; }
            if (firstDiff < 0) firstDiff = n;              // length mismatch
            // Never leave a corrupt amp selectable: clear the slot (zeros at chunk -1 = delete).
            // Capture and verify the clear-write ACK to report honestly on success/failure.
            string clearStatus = "The slot was cleared.";
            try
            {
                var clearResponse = await _client.DWriteChunkAsync(AmpList, slot, -1, new byte[128], ct);
                var ackMatch = System.Text.RegularExpressions.Regex.Match(clearResponse, "\"chunk\":(-?\\d+)}");
                if (!ackMatch.Success || int.Parse(ackMatch.Groups[1].Value) != -1)
                {
                    clearStatus = $"ATTENTION: clearing the slot may have FAILED (no ACK) — verify slot {slot} on the device before use.";
                }
            }
            catch
            {
                // Clear-write itself threw; don't mask the verify failure.
                clearStatus = $"ATTENTION: clearing the slot may have FAILED (no ACK) — verify slot {slot} on the device before use.";
            }
            throw new AmpServiceException(
                $"Read-back verify failed for slot {slot} ('{cleanName}'): first differing byte at offset {firstDiff} (chunk {firstDiff / 128 + 1}); readback {readBack.Length} B. {clearStatus}");
        }
        progress?.Report(new(AmpUploadStage.Done, totalChunks, totalChunks));
    }
}
