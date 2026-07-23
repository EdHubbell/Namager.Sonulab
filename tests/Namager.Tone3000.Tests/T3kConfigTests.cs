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
    public void Config_type_has_no_secret_key_member()
    {
        // Spec guard: the library must be INCAPABLE of holding the secret.
        Assert.DoesNotContain(typeof(T3kConfig).GetProperties(),
            p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
