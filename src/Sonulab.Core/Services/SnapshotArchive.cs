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

    /// <summary>manifest.json is small, structured text: schema/createdUtc/appVersion/device plus
    /// up to 90 slot records (30 presets + 30 amps + 30 IRs). Even generously indented that is
    /// tens of KB. 1 MiB is roughly 25x that ceiling — plenty of headroom for future fields —
    /// while still refusing to let a crafted entry inflate the process without limit.</summary>
    private const int ManifestCap = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Copies at most <paramref name="cap"/> bytes, throwing before allocating past it.
    /// A ZIP entry's central-directory Length is metadata the file itself controls — it can lie —
    /// so decompressed output is bounded here regardless of what the header claimed.</summary>
    private static void CopyAtMost(Stream source, MemoryStream destination, int cap)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > cap)
                throw new SnapshotArchiveException(
                    $"an entry decompressed past its {cap}-byte limit — the file is malformed or hostile");
            destination.Write(buffer, 0, read);
        }
    }

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
            // Reject before allocating, same reasoning as the slot blobs below: entry.Length is
            // the ZIP directory's claim, not a promise, so CopyAtMost enforces the real cap too.
            if (manifestEntry.Length > ManifestCap)
                throw new SnapshotArchiveException(
                    $"manifest.json is {manifestEntry.Length} bytes, larger than the {ManifestCap}-byte limit");

            SnapshotManifest? manifest;
            try
            {
                using var s = manifestEntry.Open();
                using var buf = new MemoryStream();
                CopyAtMost(s, buf, ManifestCap);
                buf.Position = 0;
                manifest = JsonSerializer.Deserialize<SnapshotManifest>(buf);
            }
            catch (JsonException) { throw new SnapshotArchiveException("manifest.json is not readable"); }

            if (manifest is null) throw new SnapshotArchiveException("manifest.json is empty");
            if (manifest.Schema != SnapshotManifest.CurrentSchema)
                throw new SnapshotArchiveException(
                    $"snapshot schema {manifest.Schema} was written by a newer version of NAMager " +
                    $"(this build reads schema {SnapshotManifest.CurrentSchema}).");
            // .namsnap files travel between machines, so a hand-edited or truncated manifest.json
            // must fail here with a readable reason rather than NRE deep in a view later — a
            // missing/null "device" object, a missing/null "slots" array, or a null element inside
            // it are all reachable from untrusted JSON.
            if (manifest.Device is null)
                throw new SnapshotArchiveException("manifest.json is missing its \"device\" section");
            if (manifest.Slots is null)
                throw new SnapshotArchiveException("manifest.json is missing its \"slots\" list");
            for (var i = 0; i < manifest.Slots.Count; i++)
                if (manifest.Slots[i] is null)
                    throw new SnapshotArchiveException($"manifest.json slots[{i}] is null");

            var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>();
            foreach (var slot in manifest.Slots)
            {
                var entry = zip.GetEntry(PathFor(slot.Kind, slot.Index))
                    ?? throw new SnapshotArchiveException(
                        $"manifest lists {slot.Kind} slot {slot.Index} but the file is missing");

                var expected = ExpectedLength(slot.Kind);
                // Reject before allocating: a ZIP central-directory Length is attacker-controlled
                // metadata, not a guarantee, so check it up front AND cap the actual copy — a lying
                // header must not be able to over-allocate either way. Confirmed empirically: a
                // 408 KB crafted .namsnap decompressed to ~400 MB before the old post-hoc length
                // check caught it (deflate reaches ~1000:1 on repetitive slot data).
                if (entry.Length != expected)
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} is {entry.Length} bytes, expected {expected}");

                using var s = entry.Open();
                using var buf = new MemoryStream(expected);
                CopyAtMost(s, buf, expected);
                var blob = buf.ToArray();

                if (blob.Length != expected)
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} is {blob.Length} bytes, expected {expected}");
                if (!string.Equals(ShaOf(blob), slot.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new SnapshotArchiveException(
                        $"{slot.Kind} slot {slot.Index} does not match its recorded hash — the file is damaged");

                blobs[(slot.Kind, slot.Index)] = blob;
            }
            return (manifest, blobs);
        }
    }
}
