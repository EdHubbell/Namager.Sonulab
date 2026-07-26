using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sonulab.Core.Model;

namespace Sonulab.Core.Services;

public sealed class SnapshotArchiveException(string message) : Exception(message);

/// <summary>Reads and writes the .namsnap container: a ZIP holding manifest.json plus one entry
/// per occupied slot. The same bytes go to disk on export and would go to any remote store —
/// there is one writer and one reader, so an exported file and a stored file cannot drift.
///
/// Validation is deliberately strict in both directions: a snapshot is a backup, and a backup
/// that silently loses or corrupts a slot is worse than one that refuses to be written.
///
/// ATOMICITY: Write does not guarantee atomicity on the destination stream. If it throws
/// partway through, the destination may hold a partial but syntactically valid ZIP archive.
/// A caller writing to a real file must write to a temporary path first, and rename onto the
/// final path only after Write returns successfully — never write a .namsnap directly over
/// an existing backup, as a stream fault mid-write would destroy the good backup while failing
/// to produce a new one.</summary>
public static class SnapshotArchive
{
    public const string ManifestEntry = "manifest.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static int ExpectedLength(SnapshotSlotKind kind) => kind switch
    {
        SnapshotSlotKind.Preset => 8192,
        SnapshotSlotKind.Amp => 12288,
        SnapshotSlotKind.Ir => 4096,
        _ => throw new SnapshotArchiveException($"unknown slot kind {kind}"),
    };

    private static string PathFor(SnapshotSlotKind kind, int index) => kind switch
    {
        SnapshotSlotKind.Preset => $"presets/{index:D2}.pst",
        SnapshotSlotKind.Amp => $"amps/{index:D2}.vxamp",
        SnapshotSlotKind.Ir => $"irs/{index:D2}.irblob",
        _ => throw new SnapshotArchiveException($"unknown slot kind {kind}"),
    };

    public static string ShaOf(ReadOnlySpan<byte> blob) =>
        Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

    public static void Write(Stream destination, SnapshotManifest manifest,
                             IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> blobs)
    {
        foreach (var slot in manifest.Slots)
        {
            if (!blobs.TryGetValue((slot.Kind, slot.Index), out var blob))
                throw new SnapshotArchiveException(
                    $"manifest lists {slot.Kind} slot {slot.Index} but no blob was supplied");
            if (blob.Length != ExpectedLength(slot.Kind))
                throw new SnapshotArchiveException(
                    $"{slot.Kind} slot {slot.Index} is {blob.Length} bytes, expected {ExpectedLength(slot.Kind)}");
        }

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        using (var entry = zip.CreateEntry(ManifestEntry).Open())
            JsonSerializer.Serialize(entry, manifest, Json);

        foreach (var slot in manifest.Slots)
        {
            using var entry = zip.CreateEntry(PathFor(slot.Kind, slot.Index)).Open();
            entry.Write(blobs[(slot.Kind, slot.Index)]);
        }
    }

    public static (SnapshotManifest Manifest, IReadOnlyDictionary<(SnapshotSlotKind, int), byte[]> Blobs)
        Read(Stream source)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new SnapshotArchiveException("not a valid .namsnap file"); }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestEntry)
                ?? throw new SnapshotArchiveException("no manifest.json — not a .namsnap file");

            SnapshotManifest? manifest;
            try
            {
                using var s = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<SnapshotManifest>(s);
            }
            catch (JsonException) { throw new SnapshotArchiveException("manifest.json is not readable"); }

            if (manifest is null) throw new SnapshotArchiveException("manifest.json is empty");
            if (manifest.Schema != SnapshotManifest.CurrentSchema)
                throw new SnapshotArchiveException(
                    $"snapshot schema {manifest.Schema} was written by a newer version of NAMager " +
                    $"(this build reads schema {SnapshotManifest.CurrentSchema}).");

            var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>();
            foreach (var slot in manifest.Slots)
            {
                var entry = zip.GetEntry(PathFor(slot.Kind, slot.Index))
                    ?? throw new SnapshotArchiveException(
                        $"manifest lists {slot.Kind} slot {slot.Index} but the file is missing");

                using var s = entry.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                var blob = buf.ToArray();

                if (blob.Length != ExpectedLength(slot.Kind))
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} is {blob.Length} bytes, expected {ExpectedLength(slot.Kind)}");
                if (!string.Equals(ShaOf(blob), slot.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} does not match its recorded hash — the file is damaged");

                blobs[(slot.Kind, slot.Index)] = blob;
            }
            return (manifest, blobs);
        }
    }
}
