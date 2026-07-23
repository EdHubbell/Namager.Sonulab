using Namager.Tone3000;

public class T3kConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"t3kcfg-{Guid.NewGuid():N}");
    public T3kConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteCfg(string json)
    { var p = Path.Combine(_dir, "tone3000.json"); File.WriteAllText(p, json); return p; }

    [Fact]
    public void Loads_a_valid_config()
    {
        var p = WriteCfg("""{ "publishable_key": "t3k_pub_abc123", "secret_key": "t3k_cs_x", "redirect_port": 7423 }""");
        var cfg = T3kConfig.TryLoad(p);
        Assert.NotNull(cfg);
        Assert.Equal("t3k_pub_abc123", cfg!.PublishableKey);
        Assert.Equal(7423, cfg.RedirectPort);
    }

    [Fact]
    public void Missing_file_returns_null() =>
        Assert.Null(T3kConfig.TryLoad(Path.Combine(_dir, "nope.json")));

    [Fact]
    public void Invalid_json_returns_null()
    { Assert.Null(T3kConfig.TryLoad(WriteCfg("{not json"))); }

    [Fact]
    public void Placeholder_or_empty_key_returns_null()
    {
        Assert.Null(T3kConfig.TryLoad(WriteCfg("""{ "publishable_key": "t3k_pk_YOUR_KEY_HERE", "redirect_port": 0 }""")));
        Assert.Null(T3kConfig.TryLoad(WriteCfg("""{ "publishable_key": "", "redirect_port": 0 }""")));
    }

    [Fact]
    public void Falls_back_to_the_embedded_key_when_no_config_file_exists()
    {
        // The shipped build has no %APPDATA% file: end users must still get a usable
        // client_id (and the browser sign-in card) rather than the "add your keys" card.
        var cfg = T3kConfig.LoadDefault(Path.Combine(_dir, "none.json"), Path.Combine(_dir, "alsonone.json"));
        Assert.NotNull(cfg);
        Assert.Equal(T3kConfig.EmbeddedPublishableKey, cfg!.PublishableKey);
        Assert.Equal(0, cfg.RedirectPort);
    }

    [Fact]
    public void Default_TryLoad_always_yields_a_config()
    {
        // Wiring guard for the shipped app: with no argument (how MainWindowViewModel calls it)
        // the fallback chain must reach the embedded key, even on a machine with no %APPDATA%
        // config at all — a null here is the "add your Tone3000 keys" dead end end users hit.
        Assert.NotNull(T3kConfig.TryLoad());
    }

    [Fact]
    public void Embedded_key_is_a_real_publishable_key_and_never_a_secret()
    {
        Assert.StartsWith("t3k_pub_", T3kConfig.EmbeddedPublishableKey);
        Assert.DoesNotContain("t3k_cs_", T3kConfig.EmbeddedPublishableKey);
        Assert.DoesNotContain("YOUR_KEY_HERE", T3kConfig.EmbeddedPublishableKey);
    }

    [Fact]
    public void Reads_the_legacy_config_dir_when_the_current_one_is_absent()
    {
        // Installs predating the %APPDATA%\StompStationManager -> \Namager move keep their
        // hand-entered key and redirect port instead of silently reverting to the default.
        var legacy = WriteCfg("""{ "publishable_key": "t3k_pub_legacy", "redirect_port": 7423 }""");
        var cfg = T3kConfig.LoadDefault(Path.Combine(_dir, "none.json"), legacy);
        Assert.Equal("t3k_pub_legacy", cfg!.PublishableKey);
        Assert.Equal(7423, cfg.RedirectPort);
    }

    [Fact]
    public void Current_config_dir_wins_over_legacy_and_embedded()
    {
        var current = Path.Combine(_dir, "current.json");
        File.WriteAllText(current, """{ "publishable_key": "t3k_pub_current", "redirect_port": 1 }""");
        var legacy = WriteCfg("""{ "publishable_key": "t3k_pub_legacy", "redirect_port": 2 }""");
        Assert.Equal("t3k_pub_current", T3kConfig.LoadDefault(current, legacy)!.PublishableKey);
    }

    [Fact]
    public void Config_type_has_no_secret_key_member()
    {
        // Spec guard: the library must be INCAPABLE of holding the secret.
        Assert.DoesNotContain(typeof(T3kConfig).GetProperties(),
            p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
