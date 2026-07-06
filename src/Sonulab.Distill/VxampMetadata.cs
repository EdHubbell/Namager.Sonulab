using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sonulab.Distill;

public sealed record AmpSourceInfo(string? File = null, long? Size = null,
                                   string? Modified = null, string? Sha256 = null);

public sealed record AmpDistillInfo(string? Version = null, double? ShapeErr = null);

/// <summary>Slot metadata carried in the SSMD block. Dates are ISO-8601 UTC strings.
/// Extra holds unknown top-level JSON fields so future writers' data survives our edits.</summary>
public sealed record AmpMetadata(
    AmpSourceInfo? Source = null,
    string? Uploaded = null,
    JsonObject? Nam = null,
    AmpDistillInfo? Distill = null,
    string? Notes = null,
    string? Url = null,
    JsonObject? Extra = null);

/// <summary>Reads/writes the SSMD metadata block in the padding region of a 12288-byte
/// vxamp slot (spec: docs/superpowers/specs/2026-07-06-amp-metadata-design.md).
/// Layout at Offset: "SSMD" | u16 LE version=1 | u16 LE json length | UTF-8 JSON | zero fill.
/// The DSP payload [0, Offset) is never touched. Anything unparseable reads as null.</summary>
public static class VxampMetadata
{
    public const int Offset = VxampFormat.HeaderSize + VxampFormat.BodySize;   // 8256
    public const int RegionSize = VxampFormat.SlotSize - Offset;               // 4032
    public const int BlockHeaderSize = 8;
    public const int MaxJsonBytes = RegionSize - BlockHeaderSize;              // 4024
    public const ushort Version = 1;

    private static ReadOnlySpan<byte> Magic => "SSMD"u8;
    private static readonly string[] KnownKeys = ["source", "uploaded", "nam", "distill", "notes", "url"];

    public static AmpMetadata? TryRead(ReadOnlySpan<byte> slot)
    {
        if (slot.Length != VxampFormat.SlotSize) return null;
        var r = slot.Slice(Offset, RegionSize);
        if (!r[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4, 2)) != Version) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(6, 2));
        if (n > MaxJsonBytes) return null;
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(r.Slice(BlockHeaderSize, n)))
                is JsonObject o ? FromJson(o) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Stamp the block in place. On budget overflow trims Nam first, then Notes
    /// (spec trim priority). Never modifies bytes below Offset.</summary>
    public static void Write(byte[] slot, AmpMetadata meta)
    {
        if (slot.Length != VxampFormat.SlotSize)
            throw new ArgumentException($"expected {VxampFormat.SlotSize}-byte slot, got {slot.Length}");
        var m = meta;
        var json = SerializeBytes(m);
        if (json.Length > MaxJsonBytes && m.Nam is not null)
        { m = m with { Nam = null }; json = SerializeBytes(m); }
        while (json.Length > MaxJsonBytes && !string.IsNullOrEmpty(m.Notes))
        {
            int cut = Math.Min(m.Notes.Length, Math.Max(1, json.Length - MaxJsonBytes));
            var notes = m.Notes[..^cut];
            if (notes.Length > 0 && char.IsHighSurrogate(notes[^1])) notes = notes[..^1];
            m = m with { Notes = notes.Length == 0 ? null : notes };
            json = SerializeBytes(m);
        }
        if (json.Length > MaxJsonBytes)
            throw new ArgumentException($"metadata is {json.Length} B even after trimming (max {MaxJsonBytes}).");

        Array.Clear(slot, Offset, RegionSize);
        Magic.CopyTo(slot.AsSpan(Offset));
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(Offset + 4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(slot.AsSpan(Offset + 6), (ushort)json.Length);
        json.CopyTo(slot, Offset + BlockHeaderSize);
    }

    /// <summary>Serialized size in bytes of this metadata as-is (no trimming) — the UI's budget meter.</summary>
    public static int JsonByteCount(AmpMetadata meta) => SerializeBytes(meta).Length;

    private static byte[] SerializeBytes(AmpMetadata m) => Encoding.UTF8.GetBytes(ToJson(m).ToJsonString());

    private static JsonObject ToJson(AmpMetadata m)
    {
        var o = new JsonObject();
        if (m.Extra is not null)
            foreach (var kv in m.Extra)
                o[kv.Key] = kv.Value?.DeepClone();
        if (m.Source is { } src)
        {
            var s = new JsonObject();
            if (src.File is not null) s["file"] = src.File;
            if (src.Size is not null) s["size"] = src.Size;
            if (src.Modified is not null) s["modified"] = src.Modified;
            if (src.Sha256 is not null) s["sha256"] = src.Sha256;
            o["source"] = s;
        }
        if (m.Uploaded is not null) o["uploaded"] = m.Uploaded;
        if (m.Nam is not null) o["nam"] = m.Nam.DeepClone();
        if (m.Distill is { } d)
        {
            var j = new JsonObject();
            if (d.Version is not null) j["version"] = d.Version;
            if (d.ShapeErr is not null) j["shapeErr"] = d.ShapeErr;
            o["distill"] = j;
        }
        if (m.Notes is not null) o["notes"] = m.Notes;
        if (m.Url is not null) o["url"] = m.Url;
        return o;
    }

    private static AmpMetadata FromJson(JsonObject o)
    {
        AmpSourceInfo? source = null;
        if (o["source"] is JsonObject s)
            source = new AmpSourceInfo((string?)s["file"], (long?)s["size"],
                                       (string?)s["modified"], (string?)s["sha256"]);
        AmpDistillInfo? distill = null;
        if (o["distill"] is JsonObject d)
            distill = new AmpDistillInfo((string?)d["version"], (double?)d["shapeErr"]);
        JsonObject? extra = null;
        foreach (var kv in o)
        {
            if (KnownKeys.Contains(kv.Key)) continue;
            extra ??= new JsonObject();
            extra[kv.Key] = kv.Value?.DeepClone();
        }
        return new AmpMetadata(source, (string?)o["uploaded"], o["nam"] as JsonObject is { } nam
                ? (JsonObject)nam.DeepClone() : null,
            distill, (string?)o["notes"], (string?)o["url"], extra);
    }
}
