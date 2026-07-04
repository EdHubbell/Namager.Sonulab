using System.Text;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class AmpServiceException(string message) : Exception(message);

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

    public Task RenameAmpAsync(int index, string name, CancellationToken ct = default) =>
        _client.DWriteChunkAsync(AmpList, index, -1, NamePad(ValidateName(name)), ct);

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
}
