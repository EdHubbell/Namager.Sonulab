using Namager.Tone3000;

public class T3kTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"t3ktok-{Guid.NewGuid():N}.token");
    private readonly string _legacy = Path.Combine(Path.GetTempPath(), $"t3ktok-legacy-{Guid.NewGuid():N}.token");
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_legacy)) File.Delete(_legacy);
    }

    [Fact]
    public void Roundtrips_a_token()
    {
        var store = new T3kTokenStore(_path);
        store.Save("rt_secret_value_123");
        Assert.Equal("rt_secret_value_123", store.Load());
    }

    [Fact]
    public void Stored_bytes_are_not_plaintext()
    {
        var store = new T3kTokenStore(_path);
        store.Save("rt_secret_value_123");
        var raw = File.ReadAllBytes(_path);
        Assert.DoesNotContain("rt_secret_value_123",
            System.Text.Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void Load_returns_null_when_missing() =>
        Assert.Null(new T3kTokenStore(_path).Load());

    [Fact]
    public void Load_returns_null_on_corrupted_file()
    {
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3, 4 });
        Assert.Null(new T3kTokenStore(_path).Load());
    }

    [Fact]
    public void Clear_deletes_and_is_idempotent()
    {
        var store = new T3kTokenStore(_path);
        store.Save("x");
        store.Clear();
        Assert.Null(store.Load());
        store.Clear();                                       // no throw on second clear
    }

    [Fact]
    public void Loads_the_legacy_token_when_the_current_one_is_absent()
    {
        // The config-dir move must not silently sign out an install that was signed in.
        new T3kTokenStore(_legacy).Save("rt_from_legacy");
        Assert.Equal("rt_from_legacy", new T3kTokenStore(_path, _legacy).Load());
    }

    [Fact]
    public void Current_token_wins_over_legacy()
    {
        new T3kTokenStore(_legacy).Save("rt_from_legacy");
        new T3kTokenStore(_path).Save("rt_current");
        Assert.Equal("rt_current", new T3kTokenStore(_path, _legacy).Load());
    }

    [Fact]
    public void Clear_removes_the_legacy_token_too()
    {
        // Otherwise sign-out would be undone by the fallback on the next start.
        new T3kTokenStore(_legacy).Save("rt_from_legacy");
        var store = new T3kTokenStore(_path, _legacy);
        store.Clear();
        Assert.Null(store.Load());
    }
}
