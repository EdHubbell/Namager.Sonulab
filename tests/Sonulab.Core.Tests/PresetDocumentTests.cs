using Sonulab.Core.Model;
using Xunit;

public class PresetDocumentTests
{
    static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "presets", name));

    public static IEnumerable<object[]> AllPresets() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "presets"), "*.pst")
                 .Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(AllPresets))]
    public void Parse_then_ToBytes_is_byte_identical(string name)
    {
        var bytes = Fixture(name);
        var doc = PresetDocument.Parse(bytes);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void GetValueJson_reads_a_known_value()
    {
        var doc = PresetDocument.Parse(Fixture("Pano-Verb.pst"));
        Assert.Equal("\"Pano-Verb\"", doc.GetValueJson(@"root\app\amp\amp"));
    }

    [Fact]
    public void SetValueJson_changes_value_and_keeps_8192_bytes()
    {
        var doc = PresetDocument.Parse(Fixture("Pano-Verb.pst"));
        doc.SetValueJson(@"root\app\amp\on_off", "\"OFF\"");
        Assert.Equal("\"OFF\"", doc.GetValueJson(@"root\app\amp\on_off"));
        Assert.Equal(8192, doc.ToBytes().Length);
    }
}
