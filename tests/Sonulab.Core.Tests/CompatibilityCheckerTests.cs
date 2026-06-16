using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Transport;
using Xunit;

public class CompatibilityCheckerTests
{
    static FakeSonuLink Seed(string ver = "2.5.1", int count = 30, int chunk = 128)
    {
        var link = new FakeSonuLink();
        link.SeedScalar(@"root\sys\_name", "\"AMP Station\"");
        link.SeedScalar(@"root\sys\_id", "\"abc123\"");
        link.SeedScalar(@"root\sys\_ver", $"\"{ver}\"");
        link.SeedScalar(@"root\sys\_arch", "\"ESP32S3\"");
        link.SeedScalar(@"root\sys\_license", "\"stompstation1\"");
        link.SeedBrowse(@"root\presets", $"root\\presets:{{\"value\":[],\"type\":\"list\",\"size\":8192,\"count\":{count},\"chunk\":{chunk},\"item_type\":\"pst_pst\"}}");
        link.SeedBrowse(@"root\amp",     "root\\amp:{\"value\":[],\"type\":\"list\",\"size\":12288,\"count\":30,\"chunk\":128,\"item_type\":\"vxamp\"}");
        link.SeedBrowse(@"root\ir",      "root\\ir:{\"value\":[],\"type\":\"list\",\"size\":4096,\"count\":30,\"chunk\":128,\"item_type\":\"wav_44100\"}");
        return link;
    }

    static CompatibilityChecker Checker() =>
        new(new[] { new TestedFirmware("stompstation1", "ESP32S3", "2.5.1") });

    [Fact] public async Task Tested_firmware_allows_writes()
    {
        var link = Seed(); await link.OpenAsync();
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.Tested, r.Status);
        Assert.True(r.WritesAllowed);
        Assert.Equal("2.5.1", r.Device.Version);
    }

    [Fact] public async Task Newer_untested_version_flags_UntestedNewer()
    {
        var link = Seed(ver: "2.6.0"); await link.OpenAsync();
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.UntestedNewer, r.Status);
        Assert.False(r.WritesAllowed);
    }

    [Fact] public async Task Older_untested_version_is_Unknown()
    {
        var link = Seed(ver: "2.4.0"); await link.OpenAsync();
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.Unknown, r.Status);
        Assert.False(r.WritesAllowed);
    }

    [Fact] public async Task Structural_mismatch_blocks_writes_even_if_version_tested()
    {
        var link = Seed(count: 16); await link.OpenAsync();   // wrong slot count
        var r = await Checker().CheckAsync(new SonuClient(link));
        Assert.Equal(CompatibilityStatus.StructuralMismatch, r.Status);
        Assert.False(r.WritesAllowed);
    }
}
