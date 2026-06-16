using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class BackupService
{
    private readonly DeviceRepository _repo;
    public BackupService(DeviceRepository repo) => _repo = repo;

    public async Task<int> SnapshotAllAsync(string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        int count = 0;
        foreach (var slot in await _repo.ListPresetsAsync(ct))
        {
            if (slot.IsEmpty) continue;
            var doc = await _repo.ReadPresetAsync(slot.Index, ct);
            var file = Path.Combine(folder, $"{slot.Index:D2} - {Sanitize(slot.Name)}.pst");
            await File.WriteAllBytesAsync(file, doc.ToBytes(), ct);
            count++;
        }
        return count;
    }

    public async Task RestoreSlotAsync(int index, string pstPath, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(pstPath, ct);
        var doc = PresetDocument.Parse(bytes);
        // recover the display name from the file name "NN - Name.pst", fall back to the slot's name
        var stem = Path.GetFileNameWithoutExtension(pstPath);
        int dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        var name = dash >= 0 ? stem[(dash + 3)..] : stem;
        await _repo.WritePresetToSlotAsync(index, name, doc, verify: true, ct);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
