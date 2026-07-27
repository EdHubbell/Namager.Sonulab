using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Sonulab.Core.Tests;

public class SnapshotArchiveTests
{
    private static (SnapshotManifest, Dictionary<(SnapshotSlotKind, int), byte[]>) Sample()
    {
        var preset = new byte[8192]; preset[0] = 1;
        var amp = new byte[12288]; amp[0] = 2;
        var ir = new byte[4096]; ir[0] = 3;

        var blobs = new Dictionary<(SnapshotSlotKind, int), byte[]>
        {
            [(SnapshotSlotKind.Preset, 0)] = preset,
            [(SnapshotSlotKind.Amp, 3)] = amp,
            [(SnapshotSlotKind.Ir, 11)] = ir,
        };
        var manifest = new SnapshotManifest(
            SnapshotManifest.CurrentSchema, "2026-07-26T14:02:11Z", "0.9.7",
            new SnapshotDevice("StompStation", "2.5.1"),
            [
                new SnapshotSlot(SnapshotSlotKind.Preset, 0, "Steel Clean", Sha(preset), null),
                // Amp T3k is always null (SnapshotManifest.cs, SnapshotServiceTests) — amps don't
                // carry a machine-readable Tone3000 id yet. SnapshotArchive itself is agnostic to
                // what T3k holds (it just round-trips the manifest), but this fixture should still
                // look like a real manifest, since it's the one the next author copies.
                new SnapshotSlot(SnapshotSlotKind.Amp, 3, "Dumble SS", Sha(amp), null),
                new SnapshotSlot(SnapshotSlotKind.Ir, 11, "4x12", Sha(ir), new SnapshotT3k(2468, 1357)),
            ]);
        return (manifest, blobs);
    }

    private static string Sha(byte[] b) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public void Round_trips_blobs_byte_for_byte()
    {
        var (manifest, blobs) = Sample();
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest, blobs);
        ms.Position = 0;

        var (readManifest, readBlobs) = SnapshotArchive.Read(ms);

        Assert.Equal(3, readBlobs.Count);
        Assert.Equal(blobs[(SnapshotSlotKind.Preset, 0)], readBlobs[(SnapshotSlotKind.Preset, 0)]);
        Assert.Equal(blobs[(SnapshotSlotKind.Amp, 3)], readBlobs[(SnapshotSlotKind.Amp, 3)]);
        Assert.Equal(blobs[(SnapshotSlotKind.Ir, 11)], readBlobs[(SnapshotSlotKind.Ir, 11)]);
        Assert.Equal("Steel Clean", readManifest.Slots[0].Name);
        Assert.Equal(2468, readManifest.Slots[2].T3k!.ToneId);
    }

    [Fact]
    public void Refuses_an_unknown_schema_version_rather_than_guessing()
    {
        var (manifest, blobs) = Sample();
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest with { Schema = 999 }, blobs);
        ms.Position = 0;

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Refuses_a_blob_whose_hash_does_not_match_the_manifest()
    {
        var (manifest, blobs) = Sample();
        var tampered = manifest.Slots.ToList();
        tampered[0] = tampered[0] with { Sha = new string('0', 64) };
        using var ms = new MemoryStream();
        SnapshotArchive.Write(ms, manifest with { Slots = tampered }, blobs);
        ms.Position = 0;

        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }

    [Fact]
    public void Refuses_a_blob_of_the_wrong_length()
    {
        var (manifest, blobs) = Sample();
        blobs[(SnapshotSlotKind.Ir, 11)] = new byte[100];
        using var ms = new MemoryStream();
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Write(ms, manifest, blobs));
    }

    [Fact]
    public void Refuses_a_manifest_slot_with_no_matching_blob()
    {
        var (manifest, blobs) = Sample();
        blobs.Remove((SnapshotSlotKind.Amp, 3));
        using var ms = new MemoryStream();
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Write(ms, manifest, blobs));
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_zip()
    {
        using var ms = new MemoryStream("this is not a zip"u8.ToArray());
        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }

    [Fact]
    public void Refuses_an_archive_with_no_manifest()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("presets/00.pst");
        ms.Position = 0;

        Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
    }

    // ---------- malformed manifest shapes (a .namsnap travels between machines, so a hand-edited
    // or truncated manifest.json must fail legibly, not NRE) ----------

    private static MemoryStream ZipWithRawManifest(string manifestJson)
    {
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var e = zip.CreateEntry(SnapshotArchive.ManifestEntry).Open())
            e.Write(System.Text.Encoding.UTF8.GetBytes(manifestJson));
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Refuses_a_manifest_with_no_slots_property()
    {
        using var ms = ZipWithRawManifest(
            """{"schema":1,"createdUtc":"t","appVersion":"0.9.7","device":{"model":"StompStation","fw":"2.5.1"}}""");

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("slots", ex.Message);
    }

    [Fact]
    public void Refuses_a_manifest_with_a_null_slots_property()
    {
        using var ms = ZipWithRawManifest(
            """{"schema":1,"createdUtc":"t","appVersion":"0.9.7","device":{"model":"StompStation","fw":"2.5.1"},"slots":null}""");

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("slots", ex.Message);
    }

    [Fact]
    public void Refuses_a_manifest_whose_slots_array_contains_a_null_element()
    {
        using var ms = ZipWithRawManifest(
            """{"schema":1,"createdUtc":"t","appVersion":"0.9.7","device":{"model":"StompStation","fw":"2.5.1"},"slots":[null]}""");

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("slots[0]", ex.Message);
    }

    [Fact]
    public void Refuses_a_manifest_with_no_device_property()
    {
        using var ms = ZipWithRawManifest(
            """{"schema":1,"createdUtc":"t","appVersion":"0.9.7","slots":[]}""");

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("device", ex.Message);
    }

    [Fact]
    public void Refuses_a_manifest_with_a_null_device_property()
    {
        using var ms = ZipWithRawManifest(
            """{"schema":1,"createdUtc":"t","appVersion":"0.9.7","device":null,"slots":[]}""");

        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        Assert.Contains("device", ex.Message);
    }

    // ---------- unbounded decompression (zip-bomb) guard ----------

    /// <summary>Reproduces the confirmed empirical bug: a real (not spoofed) highly-compressible
    /// entry whose declared/actual uncompressed length exceeds the slot's expected size used to be
    /// read in full via CopyTo before the length was checked — a few-MB file could allocate
    /// hundreds of MB. The fix checks entry.Length against the expected slot size BEFORE opening
    /// the entry, so a bomb must be rejected near-instantly, without the read loop ever running.</summary>
    [Fact]
    public void Refuses_an_oversize_slot_entry_without_reading_it()
    {
        var manifest = new SnapshotManifest(SnapshotManifest.CurrentSchema, "t", "0.9.7",
            new SnapshotDevice("StompStation", "2.5.1"),
            [new SnapshotSlot(SnapshotSlotKind.Ir, 0, "Bomb", new string('0', 64), null)]);

        var bomb = new byte[20_000_000]; // real bytes; all zero, so the zip itself stays tiny
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var e = zip.CreateEntry(SnapshotArchive.ManifestEntry).Open())
                System.Text.Json.JsonSerializer.Serialize(e, manifest);
            using (var e = zip.CreateEntry("irs/00.irblob", System.IO.Compression.CompressionLevel.Optimal).Open())
                e.Write(bomb);
        }
        ms.Position = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        sw.Stop();

        Assert.Contains("20000000", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"rejection took {sw.ElapsedMilliseconds}ms — looks like the full entry was decompressed first");
    }

    /// <summary>Same guard applied to manifest.json itself (finding #3 called this out separately —
    /// it was unbounded even after the slot-entry check existed).</summary>
    [Fact]
    public void Refuses_an_oversize_manifest_entry_without_reading_it()
    {
        var junk = new byte[5_000_000]; // real bytes; not valid JSON, but the cap must reject before parsing
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var e = zip.CreateEntry(SnapshotArchive.ManifestEntry, System.IO.Compression.CompressionLevel.Optimal).Open())
            e.Write(junk);
        ms.Position = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<SnapshotArchiveException>(() => SnapshotArchive.Read(ms));
        sw.Stop();

        Assert.Contains("manifest.json", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"rejection took {sw.ElapsedMilliseconds}ms — looks like the full entry was decompressed first");
    }
}
