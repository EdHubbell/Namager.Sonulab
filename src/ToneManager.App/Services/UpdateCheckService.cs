using System.Net.Http;
using System.Text.Json;

namespace ToneManager.App.Services;

public sealed record UpdateInfo(string Version, string Url);

public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);
}

/// <summary>Startup new-version check against the public GitHub Releases API.
/// Unauthenticated (60 req/hr/IP is ample for once-per-launch). Never throws and never
/// blocks startup — every failure mode returns null.</summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/EdHubbell/StompStationManager/releases/latest";

    private readonly HttpClient _http;
    private readonly string _currentVersion;

    public UpdateCheckService(HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StompStationManager");
        _currentVersion = currentVersion ?? AppInfo.Version;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(LatestReleaseUrl, ct));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) ||
                !doc.RootElement.TryGetProperty("html_url", out var urlEl))
                return null;
            var tag = tagEl.GetString();
            var url = urlEl.GetString();
            if (tag is null || url is null) return null;

            var latest = tag.TrimStart('v', 'V');
            return IsNewer(_currentVersion, latest) ? new UpdateInfo(latest, url) : null;
        }
        catch
        {
            return null;   // offline / rate-limited / malformed: silent no-op by spec
        }
    }

    /// <summary>Dev builds (any '-' suffix, e.g. 1.0.0-dev) never prompt; otherwise
    /// plain System.Version comparison. Unparseable input never prompts.</summary>
    public static bool IsNewer(string current, string latest)
    {
        if (current.Contains('-')) return false;
        return Version.TryParse(current, out var c)
            && Version.TryParse(latest, out var l)
            && l > c;
    }
}
