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
    /// <summary>The device protocol's dread/dwrite chunk size, mirrored here purely for
    /// region geometry (which 1-based chunk holds which region byte).</summary>
    public const int ProtocolChunkSize = 128;
    /// <summary>1-based chunk containing the region start (Offset 8256 → chunk 65)...</summary>
    public const int FirstRegionChunk = Offset / ProtocolChunkSize + 1;
    /// <summary>...and where the region starts inside that chunk (byte 64).</summary>
    public const int OffsetInFirstChunk = Offset % ProtocolChunkSize;

    private static ReadOnlySpan<byte> Magic => "SSMD"u8;
    private static readonly string[] KnownKeys = ["source", "uploaded", "nam", "distill", "notes", "url"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static AmpMetadata? TryRead(ReadOnlySpan<byte> slot) =>
        slot.Length == VxampFormat.SlotSize ? TryReadRegion(slot.Slice(Offset, RegionSize)) : null;

    /// <summary>Parse an SSMD block from a buffer that STARTS at slot offset 8256 (the
    /// padding region). Accepts any length; bytes beyond 8+len are ignored (zeros or
    /// unread). Same null-on-anything-malformed contract as TryRead — never throws.</summary>
    public static AmpMetadata? TryReadRegion(ReadOnlySpan<byte> region)
    {
        if (BlockLength(region) is not { } blockLen || blockLen > region.Length) return null;
        try
        {
            return JsonNode.Parse(StrictUtf8.GetString(region.Slice(BlockHeaderSize, blockLen - BlockHeaderSize)))
                is JsonObject o ? FromJson(o) : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Total block byte count (header + JSON = 8 + len) read from the first 8 bytes
    /// of a region buffer, or null when there is no valid block start (short buffer, bad
    /// magic, bad version, or len > MaxJsonBytes). Needs only the first 8 bytes.</summary>
    public static int? BlockLength(ReadOnlySpan<byte> regionStart)
    {
        if (regionStart.Length < BlockHeaderSize) return null;
        if (!regionStart[..4].SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(regionStart.Slice(4, 2)) != Version) return null;
        int n = BinaryPrimitives.ReadUInt16LittleEndian(regionStart.Slice(6, 2));
        return n > MaxJsonBytes ? null : BlockHeaderSize + n;
    }

    /// <summary>1-based chunk holding the final byte of a block of the given total length
    /// (as returned by BlockLength).</summary>
    public static int LastRegionChunk(int blockLength) =>
        (Offset + blockLength - 1) / ProtocolChunkSize + 1;

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
