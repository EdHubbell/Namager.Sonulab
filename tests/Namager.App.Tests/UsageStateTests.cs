using System.Globalization;
using Namager.App.Services;
using Xunit;

public class UsageStateTests : IDisposable
{
    // Every test's throwaway files live under one scoped temp directory; nothing touches the
    // real %APPDATA%, and the directory is removed after each test.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"usage-test-{Guid.NewGuid():N}");
    public UsageStateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string TempPath() =>
        Path.Combine(_dir, $"usage-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_on_first_run_mints_a_guid()
    {
        var state = UsageState.Load(TempPath());
        Assert.True(Guid.TryParse(state.InstallId, out _));
        Assert.Null(state.LastPingUtc);
    }

    [Fact]
    public void InstallId_is_stable_across_save_and_reload()
    {
        var path = TempPath();
        var first = UsageState.Load(path);
        first.Save(path);
        var second = UsageState.Load(path);
        Assert.Equal(first.InstallId, second.InstallId);
    }

    [Fact]
    public void LastPingUtc_round_trips()
    {
        var path = TempPath();
        var state = UsageState.Load(path) with { LastPingUtc = "2026-07-23" };
        state.Save(path);
        Assert.Equal("2026-07-23", UsageState.Load(path).LastPingUtc);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"installId\":\"\"}")]
    [InlineData("{\"installId\":\"not-a-guid\"}")]
    public void Load_treats_corrupt_file_as_first_run(string contents)
    {
        var path = TempPath();
        File.WriteAllText(path, contents);
        var state = UsageState.Load(path);
        Assert.True(Guid.TryParse(state.InstallId, out _));   // must not throw
    }

    [Fact]
    public void ShouldPing_is_false_on_the_same_day_and_true_on_a_new_one()
    {
        var state = new UsageState(Guid.NewGuid().ToString(), "2026-07-23");
        Assert.False(state.ShouldPing(new DateOnly(2026, 7, 23)));
        Assert.True(state.ShouldPing(new DateOnly(2026, 7, 24)));
    }

    [Fact]
    public void ShouldPing_is_true_when_never_pinged()
        => Assert.True(new UsageState(Guid.NewGuid().ToString(), null)
                       .ShouldPing(new DateOnly(2026, 7, 23)));

    [Fact]
    public void ShouldPing_uses_invariant_dates_under_a_non_gregorian_locale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var state = new UsageState(Guid.NewGuid().ToString(), "2026-07-23");
            Assert.False(state.ShouldPing(new DateOnly(2026, 7, 23)));
            Assert.True(state.ShouldPing(new DateOnly(2026, 7, 24)));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Save_to_an_unwritable_path_does_not_throw()
    {
        // A path whose parent is a file, not a directory - guaranteed to fail.
        var file = TempPath();
        File.WriteAllText(file, "x");
        var state = UsageState.Load(TempPath());
        state.Save(Path.Combine(file, "usage.json"));   // must not throw
    }

    [Fact]
    public void DefaultPath_sits_next_to_the_tone3000_config()
    {
        Assert.EndsWith(Path.Combine("Namager", "usage.json"), UsageState.DefaultPath);
    }

    // Guid.TryParse accepts N/B/P/X formats too, but the worker's installId check is a strict
    // 8-4-4-4-12 regex — a hand-edited (or differently-formatted) file must still normalize to
    // the canonical hyphenated lowercase "D" format on load, or that install 400-loops forever.
    [Theory]
    [InlineData("8f3c1e644b2a4c1d9e7f1a2b3c4d5e6f")]                     // N format, no hyphens
    [InlineData("{8f3c1e64-4b2a-4c1d-9e7f-1a2b3c4d5e6f}")]                // B format, braces
    [InlineData("8F3C1E64-4B2A-4C1D-9E7F-1A2B3C4D5E6F")]                  // upper-case D format
    public void Load_normalizes_the_install_id_to_canonical_hyphenated_lowercase(string raw)
    {
        var path = TempPath();
        File.WriteAllText(path, $"{{\"installId\":\"{raw}\"}}");

        var state = UsageState.Load(path);

        Assert.Equal("8f3c1e64-4b2a-4c1d-9e7f-1a2b3c4d5e6f", state.InstallId);
    }
}
