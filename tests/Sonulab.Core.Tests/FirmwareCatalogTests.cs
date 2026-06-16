using Sonulab.Core.Connection;
using Xunit;

public class FirmwareCatalogTests
{
    [Fact] public void Load_parses_entries()
    {
        var json = "[{\"license\":\"stompstation1\",\"arch\":\"ESP32S3\",\"version\":\"2.5.1\"}]";
        var list = FirmwareCatalog.Load(json);
        var fw = Assert.Single(list);
        Assert.Equal("stompstation1", fw.License);
        Assert.Equal("ESP32S3", fw.Arch);
        Assert.Equal("2.5.1", fw.Version);
    }

    [Fact] public void Default_includes_the_known_tested_firmware()
    {
        Assert.Contains(FirmwareCatalog.Default,
            f => f.License == "stompstation1" && f.Arch == "ESP32S3" && f.Version == "2.5.1");
    }
}
