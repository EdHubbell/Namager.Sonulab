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
                new SnapshotSlot(SnapshotSlotKind.Amp, 3, "Dumble SS", Sha(amp), new SnapshotT3k(11, 22)),
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
}
