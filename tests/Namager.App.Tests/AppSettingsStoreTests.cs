using Namager.App.Services;
using Xunit;

public class AppSettingsStoreTests
{
    static string TempFile() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"), "settings.json");

    [Fact] public void Load_returns_defaults_when_the_file_is_missing()
        => Assert.Equal("System", AppSettingsStore.Load(TempFile()).Theme);

    [Fact] public void Save_then_Load_round_trips_the_theme()
    {
        var path = TempFile();
        AppSettingsStore.Save(new AppSettings { Theme = "Dark" }, path);
        Assert.Equal("Dark", AppSettingsStore.Load(path).Theme);
    }

    [Fact] public void Load_returns_defaults_for_a_corrupt_file_without_throwing()
    {
        var path = TempFile();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "{ not json");
        Assert.Equal("System", AppSettingsStore.Load(path).Theme);
    }

    [Fact] public void DefaultPath_is_under_the_Namager_appdata_folder()
    {
        Assert.Contains("Namager", AppSettingsStore.DefaultPath);
        Assert.EndsWith("settings.json", AppSettingsStore.DefaultPath);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ToVariant_maps_the_explicit_choices(string theme)
        => Assert.NotEqual(Avalonia.Styling.ThemeVariant.Default, ThemeSettings.ToVariant(theme));

    [Theory]
    [InlineData("System")]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ToVariant_falls_back_to_Default(string theme)
        => Assert.Equal(Avalonia.Styling.ThemeVariant.Default, ThemeSettings.ToVariant(theme));

    [Fact] public void DefaultPath_is_computed_on_demand_and_never_throws()
    {
        var first = AppSettingsStore.DefaultPath;
        var second = AppSettingsStore.DefaultPath;
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.EndsWith("settings.json", first);
        Assert.EndsWith("settings.json", second);
    }

    [Fact] public void Load_normalises_an_explicit_null_theme_to_System()
    {
        var path = TempFile();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "{\"Theme\":null}");
        Assert.Equal("System", AppSettingsStore.Load(path).Theme);
    }
}
