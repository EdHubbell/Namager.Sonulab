using System.Text;
using System.Text.Json.Nodes;

namespace Sonulab.Distill.Tests;

public class VxampMetadataTests
{
    private static byte[] Slot()
    {
        // Deterministic non-zero payload so "payload untouched" checks are meaningful.
        var s = new byte[VxampFormat.SlotSize];
        for (int i = 0; i < VxampMetadata.Offset; i++) s[i] = (byte)(i * 31 + 7);
        return s;
    }

    private static AmpMetadata Full() => new(
        Source: new AmpSourceInfo("Bassman 5F6A.nam", 1834024, "2026-05-01T14:22:00Z", "ab34cd"),
        Uploaded: "2026-07-06T09:15:00Z",
        Nam: new JsonObject { ["name"] = "Bassman", ["modeled_by"] = "somebody" },
        Distill: new AmpDistillInfo("1.0.0", 0.043),
        Notes: "warm clean tone",
        Url: "https://tonehunt.org/models/xyz");

    [Fact]
    public void Roundtrip_preserves_all_fields()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.Equal("Bassman 5F6A.nam", m!.Source!.File);
        Assert.Equal(1834024, m.Source.Size);
        Assert.Equal("2026-05-01T14:22:00Z", m.Source.Modified);
        Assert.Equal("ab34cd", m.Source.Sha256);
        Assert.Equal("2026-07-06T09:15:00Z", m.Uploaded);
        Assert.Equal("Bassman", (string?)m.Nam!["name"]);
        Assert.Equal("somebody", (string?)m.Nam["modeled_by"]);
        Assert.Equal("1.0.0", m.Distill!.Version);
        Assert.Equal(0.043, m.Distill.ShapeErr!.Value, 12);
        Assert.Equal("warm clean tone", m.Notes);
        Assert.Equal("https://tonehunt.org/models/xyz", m.Url);
    }

    [Fact]
    public void Write_never_touches_the_payload()
    {
        var slot = Slot();
        var before = slot[..VxampMetadata.Offset].ToArray();
        VxampMetadata.Write(slot, Full());
        Assert.Equal(before, slot[..VxampMetadata.Offset]);
    }

    [Fact]
    public void Write_zeroes_the_region_before_stamping()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full());               // long block
        VxampMetadata.Write(slot, new AmpMetadata(Notes: "x"));   // much shorter block
        var m = VxampMetadata.TryRead(slot);
        Assert.Equal("x", m!.Notes);
        Assert.Null(m.Source);                            // no residue of the old block
    }

    [Theory]
    [InlineData(0)]   // all-zero padding (every VoidX-written slot)
    [InlineData(1)]   // bad magic
    [InlineData(2)]   // unsupported version
    [InlineData(3)]   // length overruns the region
    [InlineData(4)]   // invalid JSON
    [InlineData(5)]   // valid JSON but not an object
    public void TryRead_tolerates_garbage(int kind)
    {
        var slot = Slot();
        if (kind >= 1)
        {
            VxampMetadata.Write(slot, Full());
            switch (kind)
            {
                case 1: slot[VxampMetadata.Offset] = (byte)'X'; break;
                case 2: slot[VxampMetadata.Offset + 4] = 99; break;
                case 3:
                    slot[VxampMetadata.Offset + 6] = 0xFF;   // len = 0xFFFF > 4024
                    slot[VxampMetadata.Offset + 7] = 0xFF;
                    break;
                case 4:
                    Encoding.UTF8.GetBytes("{not json!").CopyTo(slot, VxampMetadata.Offset + 8);
                    break;
                case 5:
                    var arr = Encoding.UTF8.GetBytes("[1,2,3]");
                    Array.Clear(slot, VxampMetadata.Offset + 8, 64);
                    arr.CopyTo(slot, VxampMetadata.Offset + 8);
                    slot[VxampMetadata.Offset + 6] = (byte)arr.Length;
                    slot[VxampMetadata.Offset + 7] = 0;
                    break;
            }
        }
        Assert.Null(VxampMetadata.TryRead(slot));
    }

    [Fact]
    public void TryRead_rejects_wrong_slot_size()
    {
        Assert.Null(VxampMetadata.TryRead(new byte[100]));
    }

    [Fact]
    public void Overflow_drops_nam_first()
    {
        var bigNam = new JsonObject { ["blob"] = new string('n', 5000) };
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Nam = bigNam });
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.Null(m!.Nam);                              // dropped
        Assert.Equal("warm clean tone", m.Notes);          // kept intact
    }

    [Fact]
    public void Overflow_then_truncates_notes()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Nam = null, Notes = new string('a', 5000) });
        var m = VxampMetadata.TryRead(slot);
        Assert.NotNull(m);
        Assert.NotNull(m!.Notes);
        Assert.True(m.Notes!.Length < 5000);
        Assert.True(VxampMetadata.JsonByteCount(m) <= VxampMetadata.MaxJsonBytes);
        Assert.Equal("https://tonehunt.org/models/xyz", m.Url);   // other fields survive
    }

    [Fact]
    public void Unknown_top_level_fields_are_preserved()
    {
        var slot = Slot();
        VxampMetadata.Write(slot, Full() with { Extra = new JsonObject { ["future"] = 42 } });
        var m = VxampMetadata.TryRead(slot);
        Assert.Equal(42, (int?)m!.Extra!["future"]);
        // Re-writing (the edit path) keeps them too.
        VxampMetadata.Write(slot, m with { Notes = "edited" });
        var m2 = VxampMetadata.TryRead(slot);
        Assert.Equal(42, (int?)m2!.Extra!["future"]);
        Assert.Equal("edited", m2.Notes);
    }

    [Fact]
    public void JsonByteCount_matches_what_write_stores()
    {
        var slot = Slot();
        var meta = Full();
        VxampMetadata.Write(slot, meta);
        int stored = slot[VxampMetadata.Offset + 6] | (slot[VxampMetadata.Offset + 7] << 8);
        Assert.Equal(VxampMetadata.JsonByteCount(meta), stored);
    }
}
