using ToneManager.App;

public class AppInfoTests
{
    [Fact]
    public void Version_is_the_csproj_version_without_sourcelink_hash()
    {
        // Local/test builds carry the csproj default. CI overrides with -p:Version=<tag>.
        Assert.Equal("1.0.0-dev", AppInfo.Version);
        Assert.DoesNotContain("+", AppInfo.Version);
    }
}
