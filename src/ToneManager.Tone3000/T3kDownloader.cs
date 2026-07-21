using System.Net;

namespace ToneManager.Tone3000;

public interface IT3kDownloader
{
    /// <summary>Download the model file, return the local path. Skips the network when the
    /// file already exists. Atomic: a failed download leaves nothing behind.</summary>
    Task<string> DownloadAsync(T3kModel model, string? toneFormat = null, CancellationToken ct = default);
}

public sealed class T3kDownloader(IT3kAuth auth, string? targetDir = null, HttpMessageHandler? handler = null) : IT3kDownloader
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler());
    private readonly string _dir = targetDir ?? Path.Combine("NAMFiles", "Tone3000");

    public async Task<string> DownloadAsync(T3kModel model, string? toneFormat = null, CancellationToken ct = default)
    {
        if (model.ModelUrl is not { } url)
            throw new T3kException($"'{model.Name}' has no downloadable file.", T3kError.Api);
        if (!IsTrustedDownloadUrl(url))
            throw new T3kException($"'{model.Name}' has an untrusted download URL.", T3kError.Api);

        string ext = DeriveExtension(toneFormat, model.Format, url);
        // Sanitize/truncate the NAME portion first, THEN always append "-{id}" — truncating the
        // combined "{name}-{id}" string let a >=100-char name swallow the id suffix, so two
        // long-named models collided on the same file (skip-existing then returned the wrong one).
        string safe = $"{Sanitize(model.Name ?? "")}-{model.Id}";
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, safe + ext);
        if (File.Exists(path)) return path;

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", await auth.GetAccessTokenAsync(ct));
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException e)
        { throw new T3kException("Download failed — could not reach the file server.", T3kError.Network, e); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new T3kException($"Download failed (HTTP {(int)resp.StatusCode}).", T3kError.Api);

            string tmp = path + ".part";
            try
            {
                await using (var fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs, ct);
                File.Move(tmp, path, overwrite: true);           // atomic publish
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return path;
        }
    }

    /// <summary>Only attach the user's Bearer token to a URL we trust: absolute https on
    /// tone3000.com or a subdomain (e.g. cdn.tone3000.com). Otherwise a server-controlled
    /// ModelUrl on any host would receive the token verbatim.</summary>
    private static bool IsTrustedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("www.tone3000.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".tone3000.com", StringComparison.OrdinalIgnoreCase));

    private static string DeriveExtension(string? toneFormat, string? modelFormat, string url)
    {
        // (1) toneFormat is "ir" or "wav" → .wav
        if (toneFormat is "ir" or "wav")
            return ".wav";

        // (2) model.Format is "ir" or "wav" → .wav
        if (modelFormat is "ir" or "wav")
            return ".wav";

        // (3) ModelUrl path ends with recognizable extension (.wav or .nam, case-insensitive, ignoring query string)
        var path = new Uri(url).AbsolutePath;
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return ".wav";
        if (path.EndsWith(".nam", StringComparison.OrdinalIgnoreCase))
            return ".nam";

        // (4) default to .nam
        return ".nam";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        return s.Length == 0 ? "model" : s.Length > 100 ? s[..100] : s;
    }
}
