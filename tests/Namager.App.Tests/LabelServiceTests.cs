using Namager.App.Services;
using Xunit;

public class LabelServiceTests
{
    static LabelService Svc() => new(new Dictionary<string, string>
    {
        [@"root\app\delay\tcfolder"] = "Tone and Character",
    });

    [Fact] public void Uses_json_map_when_present() =>
        Assert.Equal("Tone and Character", Svc().Label(@"root\app\delay\tcfolder", "Tone and Character (dev)"));

    [Fact] public void Falls_back_to_device_desc() =>
        Assert.Equal("Threshold", Svc().Label(@"root\app\gate\threshold", "Threshold"));

    [Fact] public void Falls_back_to_prettified_segment_when_no_desc()
    {
        Assert.Equal("Lo Cut", Svc().Label(@"root\app\ir\lo_cut", null));
        Assert.Equal("Lo Cut", Svc().Label(@"root\app\ir\lo_cut", ""));
    }

    [Fact] public void Default_loads_embedded_map() =>
        Assert.False(string.IsNullOrEmpty(LabelService.Default.Label(@"root\app\gate", "Gate")));

    [Fact] public void Embedded_labels_rename_the_mod_block_to_Modulation()
    {
        // The firmware calls it "Mod". Every other node under it self-describes correctly, so this
        // is the only override the Modulation block needs.
        Assert.Equal("Modulation", LabelService.Default.Label(@"root\app\mod", "Mod"));
    }
}
