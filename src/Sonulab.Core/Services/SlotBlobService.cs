using System.Text;
using System.Text.RegularExpressions;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

/// <summary>Identifies a blob-slot list's on-device shape (path, chunk/size layout) plus the
/// user-facing/backup-file vocabulary. Amp = root\amp (96 chunks x128B = 12288B payload),
/// Ir = root\ir (32 chunks x128B = 4096B payload).</summary>
public sealed record SlotBlobKind(string ListPath, int Chunks, int SlotBytes, string Noun, string BackupPrefix, string BackupExtension)
{
    public static readonly SlotBlobKind Amp = new(@"root\amp", 96, 12288, "Amp", "amp", ".vxamp");
    public static readonly SlotBlobKind Ir = new(@"root\ir", 32, 4096, "IR", "ir", ".irblob");
}

// Member ORDER must match AmpUploadStage (AmpService.cs) — AmpService casts between them.
public enum SlotUploadStage { BackingUp, Writing, Verifying, Done }

/// <summary>Progress for the guarded upload. ChunksTotal is always Kind.Chunks + 2
/// (chunk 0 = name, 1..Chunks = payload, -1 = commit).</summary>
public sealed record SlotUploadProgress(SlotUploadStage Stage, int ChunksDone, int ChunksTotal);

/// <summary>Guarded slot-blob operations on a 30-slot device list (root\amp, root\ir). Mirrors
/// DeviceRepository's shape. The write discipline (backup -> ACK-verified chunked write ->
/// name-at-chunk:-1 commit -> read-back verify) is lifted from the hardware-verified
/// tools/HwCheck path; this class is the ONE hardware-verified implementation of that
/// sequence — AmpService (and any future IrService) are thin fronts over it.</summary>
public sealed class SlotBlobService
{
    public const int SlotCount = 30;
    public const int NameMaxChars = 31;          // device name cap

    private readonly SonuClient _client;
    private readonly SlotBlobKind _kind;
    private readonly string _backupDir;
    private readonly Func<string, Exception> _raise;
    private readonly int _paceMs;                // extra delay between ACKed chunks
    private readonly int _settleMs;              // flash-settle pause before verify readback

    public SlotBlobService(SonuClient client, SlotBlobKind kind, string backupDir, Func<string, Exception> raise, int paceMs = 25, int settleMs = 750)
    { _client = client; _kind = kind; _backupDir = backupDir; _raise = raise; _paceMs = paceMs; _settleMs = settleMs; }

    public async Task<IReadOnlyList<SlotEntry>> ListAsync(CancellationToken ct = default)
    {
        var names = await _client.ReadListAsync(_kind.ListPath, ct);
        var slots = new List<SlotEntry>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(new SlotEntry(i, i < names.Count ? names[i] : ""));
        return slots;
    }

    public Task<byte[]> ReadAsync(int index, CancellationToken ct = default) =>
        ReadValidatedAsync(index, ct);

    /// <summary>Dread the full slot and FAIL LOUDLY on a short blob (dropped/garbled chunk on
    /// the serial link). A silently short read cached as "no metadata" once led a later
    /// metadata save to wipe an on-device SSMD block (slot-26 incident, 2026-07-06).</summary>
    private async Task<byte[]> ReadValidatedAsync(int index, CancellationToken ct)
    {
        var blob = await _client.DReadBlobAsync(_kind.ListPath, index, _kind.Chunks, ct);
        if (blob.Length != _kind.SlotBytes)
            throw _raise($"{_kind.Noun} slot {index} read returned {blob.Length} B (expected {_kind.SlotBytes}) — a chunk was dropped or garbled on the serial link. Try again.");
        return blob;
    }

    public async Task DeleteAsync(int index, CancellationToken ct = default)
    {
        var names = await _client.ReadListAsync(_kind.ListPath, ct);
        if (index < 0 || index >= names.Count || string.IsNullOrEmpty(names[index]))
            return;                                                       // already empty: no-op
        await BackupSlotAsync(index, "-deleted", ct);
        await _client.DWriteChunkAsync(_kind.ListPath, index, -1, new byte[128], ct);
    }

    public Task RenameAsync(int index, string name, CancellationToken ct = default)
    {
        if (index is < 0 or >= SlotCount)
            throw _raise($"Slot must be 0..{SlotCount - 1}, got {index}.");
        return _client.DWriteChunkAsync(_kind.ListPath, index, -1, NamePad(ValidateName(name)), ct);
    }

    /// <summary>Dread the slot and save it under the backup dir. Callers must ensure the
    /// slot is OCCUPIED first — dreading an empty slot is one timeout per chunk and is the
    /// prime suspect for killing a following commit (see HwCheck upload notes).</summary>
    private async Task<byte[]> BackupSlotAsync(int index, string suffix, CancellationToken ct)
    {
        Directory.CreateDirectory(_backupDir);
        // Validated read: a truncated backup silently written to disk right before an
        // overwrite would be a corrupt "safety net" — abort the whole operation instead.
        var blob = await ReadValidatedAsync(index, ct);
        var path = Path.Combine(_backupDir, $"{_kind.BackupPrefix}-{index}-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}{_kind.BackupExtension}");
        await File.WriteAllBytesAsync(path, blob, ct);
        return blob;
    }

    /// <summary>Trim + validate a slot name: non-empty, ASCII, truncated to 31 chars.</summary>
    public string ValidateName(string name) => ValidateName(name, _kind, _raise);

    /// <summary>Trim + validate a slot name: non-empty, ASCII, truncated to 31 chars.
    /// Static so fronts (e.g. AmpService.ValidateName) can call it without constructing an instance.</summary>
    public static string ValidateName(string name, SlotBlobKind kind, Func<string, Exception> raise)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw raise($"{kind.Noun} name must not be empty.");
        if (n.Any(ch => ch > 127)) throw raise($"{kind.Noun} name must be ASCII.");
        return n.Length > NameMaxChars ? n[..NameMaxChars] : n;
    }

    public static byte[] NamePad(string name)
    {
        var buf = new byte[128];
        var b = Encoding.ASCII.GetBytes(name);
        Array.Copy(b, buf, Math.Min(b.Length, 128));
        return buf;
    }

    /// <summary>Guarded upload (the hardware-verified HwCheck sequence):
    /// backup (occupied slots only — NEVER dread an empty slot) -> chunk 0 = name ->
    /// chunks 1..Chunks = payload -> chunk -1 = the NAME AGAIN (the commit; zeros there would
    /// delete). Every chunk's ACK is checked; abort on mismatch leaves the slot uncommitted.
    /// Read-back verify; on mismatch the slot is CLEARED so a corrupt slot is never selectable.</summary>
    public async Task UploadAsync(int slot, byte[] payload, string name,
        IProgress<SlotUploadProgress>? progress = null, CancellationToken ct = default)
    {
        if (slot is < 0 or >= SlotCount)
            throw _raise($"Slot must be 0..{SlotCount - 1}, got {slot}.");
        if (payload.Length != _kind.SlotBytes)
            throw _raise($"Expected a {_kind.SlotBytes}-byte {_kind.BackupExtension} payload, got {payload.Length} B.");
        var cleanName = ValidateName(name);
        var nameBuf = NamePad(cleanName);
        int totalChunks = _kind.Chunks + 2;                        // name + payload + commit

        // 1. Backup — ONLY if the name table says the slot is occupied. Skipping the dread on
        // empty slots is not an optimization: a full-slot dread right before the write burst is
        // the prime suspect for the commit being silently discarded (HwCheck finding).
        var names = await _client.ReadListAsync(_kind.ListPath, ct);
        if (slot >= 0 && slot < names.Count && !string.IsNullOrEmpty(names[slot]))
        {
            progress?.Report(new(SlotUploadStage.BackingUp, 0, totalChunks));
            await BackupSlotAsync(slot, "", ct);
        }

        // 2. ACK-verified write burst.
        int done = 0;
        async Task WriteChunkAckedAsync(int chunk, byte[] data, int expectNext)
        {
            var raw = await _client.DWriteChunkAsync(_kind.ListPath, slot, chunk, data, ct);
            var m = Regex.Match(raw, "\"chunk\":(-?\\d+)}");
            if (!m.Success || int.Parse(m.Groups[1].Value) != expectNext)
                throw _raise(
                    $"Device ACK missing/mismatched at chunk {chunk}: got '{(m.Success ? m.Groups[1].Value : "none")}', expected {expectNext}. Upload aborted before commit; slot {slot} is unchanged.");
            progress?.Report(new(SlotUploadStage.Writing, ++done, totalChunks));
            if (_paceMs > 0) await Task.Delay(_paceMs, ct);
        }

        await WriteChunkAckedAsync(0, nameBuf, 1);
        var chunk128 = new byte[128];
        for (int chk = 1; chk <= _kind.Chunks; chk++)
        {
            Array.Copy(payload, (chk - 1) * 128, chunk128, 0, 128);
            await WriteChunkAckedAsync(chk, chunk128, chk < _kind.Chunks ? chk + 1 : -1);
        }
        await WriteChunkAckedAsync(-1, nameBuf, -1);       // the commit

        // 3. Read-back verify (after a flash-settle pause).
        progress?.Report(new(SlotUploadStage.Verifying, totalChunks, totalChunks));
        if (_settleMs > 0) await Task.Delay(_settleMs, ct);
        var readBack = await _client.DReadBlobAsync(_kind.ListPath, slot, _kind.Chunks, ct);
        if (!readBack.AsSpan().SequenceEqual(payload))
        {
            int firstDiff = -1;
            int n = Math.Min(readBack.Length, payload.Length);
            for (int i = 0; i < n; i++) if (readBack[i] != payload[i]) { firstDiff = i; break; }
            if (firstDiff < 0) firstDiff = n;              // length mismatch
            // Never leave a corrupt slot selectable: clear the slot (zeros at chunk -1 = delete).
            // Capture and verify the clear-write ACK to report honestly on success/failure.
            string clearStatus = "The slot was cleared.";
            try
            {
                var clearResponse = await _client.DWriteChunkAsync(_kind.ListPath, slot, -1, new byte[128], ct);
                var ackMatch = Regex.Match(clearResponse, "\"chunk\":(-?\\d+)}");
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
            throw _raise(
                $"Read-back verify failed for slot {slot} ('{cleanName}'): first differing byte at offset {firstDiff} (chunk {firstDiff / 128 + 1}); readback {readBack.Length} B. {clearStatus}");
        }
        progress?.Report(new(SlotUploadStage.Done, totalChunks, totalChunks));
    }
}
