using System.Net;
using Sonulab.Tone3000;

public class T3kDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"t3kdl-{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class FakeAuth : IT3kAuth
    {
        public bool IsSignedIn => true;
        public string? Username => "ed";
        public Task SignInAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignOut() { }
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("at_test");
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public byte[] Payload { get; set; } = "NAMDATA"u8.ToArray();
        public string? LastBearer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Calls++;
            LastBearer = req.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Payload) });
        }
    }

    private static T3kModel Model(string name = "Clean Deluxe", string? format = null, string? url = "https://cdn.tone3000.com/models/9") =>
        new(9, name, format, url);

    [Fact]
    public async Task Downloads_with_bearer_and_returns_the_path()
    {
        var h = new FileHandler();
        var dl = new T3kDownloader(new FakeAuth(), _dir, h);
        var path = await dl.DownloadAsync(Model());
        Assert.Equal("at_test", h.LastBearer);
        Assert.True(File.Exists(path));
        Assert.Equal("NAMDATA"u8.ToArray(), File.ReadAllBytes(path));
        Assert.EndsWith(".nam", path);
        Assert.StartsWith(_dir, path);
    }

    [Fact]
    public async Task Ir_format_gets_wav_extension()
    {
        var dl = new T3kDownloader(new FakeAuth(), _dir, new FileHandler());
        var path = await dl.DownloadAsync(Model(format: null), toneFormat: "ir");
        Assert.EndsWith(".wav", path);
    }

    [Fact]
    public async Task Wav_extension_from_model_url_wins_when_no_format()
    {
        var dl = new T3kDownloader(new FakeAuth(), _dir, new FileHandler());
        var path = await dl.DownloadAsync(Model(format: null, url: "https://cdn.tone3000.com/files/cab.WAV?sig=abc"), toneFormat: null);
        Assert.EndsWith(".wav", path);
    }

    [Fact]
    public async Task Filenames_are_sanitized()
    {
        var dl = new T3kDownloader(new FakeAuth(), _dir, new FileHandler());
        var path = await dl.DownloadAsync(Model(name: @"AC/DC <Live> ""Tone""?"));
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(Path.GetFileName(path), Path.GetInvalidFileNameChars().Select(c => c.ToString()));
    }

    [Fact]
    public async Task Existing_file_is_not_redownloaded()
    {
        var h = new FileHandler();
        var dl = new T3kDownloader(new FakeAuth(), _dir, h);
        var p1 = await dl.DownloadAsync(Model());
        var p2 = await dl.DownloadAsync(Model());
        Assert.Equal(p1, p2);
        Assert.Equal(1, h.Calls);
    }

    [Fact]
    public async Task Failed_download_leaves_no_partial_file()
    {
        var h = new FileHandler { Status = HttpStatusCode.NotFound };
        var dl = new T3kDownloader(new FakeAuth(), _dir, h);
        await Assert.ThrowsAsync<T3kException>(() => dl.DownloadAsync(Model()));
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir).Length > 0);
    }

    [Fact]
    public async Task Model_without_url_throws_api_error()
    {
        var dl = new T3kDownloader(new FakeAuth(), _dir, new FileHandler());
        var ex = await Assert.ThrowsAsync<T3kException>(
            () => dl.DownloadAsync(new T3kModel(1, "x", null, null)));
        Assert.Equal(T3kError.Api, ex.Kind);
    }

    [Fact]
    public async Task Untrusted_download_url_is_rejected_without_sending_the_token()
    {
        var h = new FileHandler();
        var dl = new T3kDownloader(new FakeAuth(), _dir, h);
        var ex = await Assert.ThrowsAsync<T3kException>(
            () => dl.DownloadAsync(Model(url: "http://evil.example/m")));
        Assert.Equal(T3kError.Api, ex.Kind);
        Assert.Equal(0, h.Calls);                             // rejected before the token was ever attached
    }

    [Fact]
    public async Task Long_names_do_not_collide_across_models()
    {
        var longName = new string('A', 150);
        var dl = new T3kDownloader(new FakeAuth(), _dir, new FileHandler());
        var m1 = new T3kModel(1, longName, null, "https://cdn.tone3000.com/models/1");
        var m2 = new T3kModel(2, longName, null, "https://cdn.tone3000.com/models/2");

        var p1 = await dl.DownloadAsync(m1);
        var p2 = await dl.DownloadAsync(m2);

        Assert.NotEqual(p1, p2);
    }
}
