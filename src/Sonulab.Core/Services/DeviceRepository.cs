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

    /// <summary>Head-read window: the amp ref sits near chunk 7, primary IR near 11, secondary
    /// IR near 23 (measured, real captures — see the 2026-07-24 perf handoff). 32 gives slack
    /// for value-length drift; past it we assume an unexpected layout and fall back to a full read.</summary>
    public const int HeadChunkCap = 32;

    public async Task<IReadOnlyList<PresetSlot>> ListPresetsBackgroundAsync(CancellationToken ct = default)
    {
        var names = await _client.ReadListBackgroundAsync(PresetsList, ct);
        var slots = new List<PresetSlot>(SlotCount);
        for (int i = 0; i < SlotCount; i++)
            slots.Add(new PresetSlot(i, i < names.Count ? names[i] : ""));
        return slots;
    }

    /// <summary>Reads only the HEAD of a preset document — chunk by chunk until the amp and both
    /// IR reference lines are complete (<see cref="PresetUsageMap.HeadComplete"/>), the content-end
    /// NUL appears, or <see cref="HeadChunkCap"/> is hit (then: full-read fallback). Built for the
    /// preset-usage scan: ~14–25 chunks instead of 64 (~2.5×). <paramref name="background"/>=true
    /// rides the SonuClient background lane (default; the scan must yield to user bursts);
    /// false uses the foreground lane (EnsureCompleteAsync's urgent finish).</summary>
    public async Task<PresetDocument> ReadPresetHeadAsync(int index, bool background = true, CancellationToken ct = default)
    {
        Task<byte[]> ReadChunks(int first, int count) => background
            ? _client.DReadChunkRangeBackgroundAsync(PresetsList, index, first, count, ct)
            : _client.DReadChunkRangeAsync(PresetsList, index, first, count, ct);

        var bytes = new List<byte>(HeadChunkCap * 128);
        for (int chunk = 1; chunk <= HeadChunkCap; chunk++)
        {
            var seg = await ReadChunks(chunk, 1);
            bytes.AddRange(seg);
            // Content ends at the first NUL (the rest of the blob is zero padding) — or a torn
            // chunk came back empty; either way there is nothing more to learn from this slot.
            if (seg.Length == 0 || Array.IndexOf(seg, (byte)0) >= 0)
                return PresetDocument.Parse(bytes.ToArray());
            if (PresetUsageMap.HeadComplete(System.Text.Encoding.ASCII.GetString(bytes.ToArray())))
                return PresetDocument.Parse(bytes.ToArray());
        }
        // Unexpected layout (refs not found in the head window): fall back to the full document
        // so the guard logic never runs on silently truncated data.
        var rest = await ReadChunks(HeadChunkCap + 1, PresetChunks - HeadChunkCap);
        bytes.AddRange(rest);
        return PresetDocument.Parse(bytes.ToArray());
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
