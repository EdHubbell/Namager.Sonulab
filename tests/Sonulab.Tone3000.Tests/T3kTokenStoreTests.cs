using Sonulab.Tone3000;

public class T3kTokenStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"t3ktok-{Guid.NewGuid():N}.token");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

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
}
