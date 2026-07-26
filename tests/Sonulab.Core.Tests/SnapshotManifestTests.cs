using System.Text.Json;
using Sonulab.Core.Model;

namespace Sonulab.Core.Tests;

public class SnapshotManifestTests
{
    private static SnapshotManifest Sample() => new(
        SnapshotManifest.CurrentSchema, "2026-07-26T14:02:11Z", "0.9.7",
        new SnapshotDevice("StompStation", "2.5.1"),
        [
            new SnapshotSlot(SnapshotSlotKind.Preset, 0, "Steel Clean", "aa", null),
            new SnapshotSlot(SnapshotSlotKind.Ir, 11, "4x12 Green", "bb", new SnapshotT3k(2468, 1357)),
        ]);

    [Fact]
    public void Round_trips_through_json_preserving_every_field()
    {
        var json = JsonSerializer.Serialize(Sample());
        var back = JsonSerializer.Deserialize<SnapshotManifest>(json)!;

        Assert.Equal(1, back.Schema);
        Assert.Equal("StompStation", back.Device.Model);
        Assert.Equal(2, back.Slots.Count);
        Assert.Equal(SnapshotSlotKind.Ir, back.Slots[1].Kind);
        Assert.Equal(2468, back.Slots[1].T3k!.ToneId);
        Assert.Null(back.Slots[0].T3k);
    }

    [Fact]
    public void Kind_serializes_as_a_lowercase_string_not_an_integer()
    {
        Assert.Contains("\"ir\"", JsonSerializer.Serialize(Sample()));
        Assert.DoesNotContain("\"kind\":2", JsonSerializer.Serialize(Sample()));
    }
}
