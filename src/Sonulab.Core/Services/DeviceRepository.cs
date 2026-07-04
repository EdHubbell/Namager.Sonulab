using Sonulab.Core.Model;
using Sonulab.Core.Protocol;

namespace Sonulab.Core.Services;

public sealed class DeviceRepository
{
    public const int SlotCount = 30;
    public const int PresetChunks = 64;          // 8192 / 128
    private const string PresetsList = @"root\presets";
    private const string PresetNode = @"root\app\preset";

    private readonly SonuClient _client;
    public DeviceRepository(SonuClient client) => _client = client;

    public async Task<IReadOnlyList<PresetSlot>> ListPresetsAsync(CancellationToken ct = default)
    {
        var names = await _client.ReadListAsync(PresetsList, ct);
        var slots = new List<PresetSlot>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(new PresetSlot(i, i < names.Count ? names[i] : ""));
        return slots;
    }

    public Task SelectPresetAsync(string name, CancellationToken ct = default) =>
        _client.WriteAsync(PresetNode, JsonString.Quote(name), ct);

    public Task SaveCurrentAsAsync(string name, CancellationToken ct = default) =>
        _client.SaveAsync(PresetNode, name, ct);

    public Task RenameAsync(int index, string name, CancellationToken ct = default) =>
        _client.DWriteChunkAsync(PresetsList, index, -1, NamePad(name), ct);

    public Task DeleteAsync(int index, CancellationToken ct = default) =>
        _client.DWriteChunkAsync(PresetsList, index, -1, new byte[128], ct);

    public async Task<PresetDocument> ReadPresetAsync(int index, CancellationToken ct = default)
    {
        var bytes = await _client.DReadBlobAsync(PresetsList, index, PresetChunks, ct);
        return PresetDocument.Parse(bytes);
    }

    /// <summary>
    /// Writes <paramref name="doc"/> into slot <paramref name="index"/> via name → replay → save → verify.
    /// PRECONDITION: <paramref name="name"/> must be UNIQUE across all occupied slots — the device's
    /// save-by-name matches the first slot with that name, so a duplicate name would save to the wrong slot.
    /// (Plan 3b's reorder uses temporary unique names during a shuffle to honor this.)
    /// </summary>
    public async Task WritePresetToSlotAsync(int index, string name, PresetDocument doc, bool verify = true, CancellationToken ct = default)
    {
        // 1) name the target slot so save-by-name lands here
        await RenameAsync(index, name, ct);
        // 2) replay the document's app params into live state
        foreach (var line in doc.Lines)
        {
            if (!NodeRecord.TryParse(line, out var rec)) continue;
            if (!rec.Path.StartsWith(@"root\app", StringComparison.Ordinal)) continue;
            if (!rec.Json.TryGetProperty("value", out var v)) continue;
            await _client.WriteAsync(rec.Path, v.GetRawText(), ct);
        }
        // 3) save live state into the slot named `name`
        await SaveCurrentAsAsync(name, ct);
        // 4) verify by reading the slot back
        if (verify)
        {
            var back = await ReadPresetAsync(index, ct);
            if (!back.ToBytes().AsSpan().SequenceEqual(doc.ToBytes()))
                throw new InvalidOperationException($"Write-back verify failed for slot {index} ('{name}').");
        }
    }

    public async Task DuplicateAsync(int sourceIndex, int destIndex, string newName, CancellationToken ct = default)
    {
        var doc = await ReadPresetAsync(sourceIndex, ct);
        await WritePresetToSlotAsync(destIndex, newName, doc, verify: true, ct);
    }

    private static byte[] NamePad(string name)
    {
        var buf = new byte[128];
        var b = System.Text.Encoding.ASCII.GetBytes(name);
        // Preset names are ASCII and fit the device's 128-byte name field; longer names are truncated.
        Array.Copy(b, buf, Math.Min(b.Length, 128));
        return buf;
    }
}
