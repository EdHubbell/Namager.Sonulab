using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;

namespace Namager.App.Services;

public interface IUsagePingService
{
    /// <summary>Best-effort anonymous ping. Never throws.</summary>
    Task PingAsync(string firmware, string? transport, CancellationToken ct = default);
}

/// <summary>Sends one anonymous ping per install per UTC day when a pedal connects, so we can
/// tell whether the app has returning users (spec: docs/superpowers/specs/2026-07-23-usage-telemetry-design.md).
/// The entire payload is installId / appVersion / fw / transport — see PRIVACY.md, which must
/// stay in sync with this file.
/// Same contract as UpdateCheckService: never throws, never blocks, silent on every failure.</summary>
public sealed class UsagePingService : IUsagePingService
{
    /// <summary>The /ping route on the same worker that serves feedback at "/".</summary>
    public const string EndpointUrl = "https://namager-sonulab-feedback.ed-eed.workers.dev/ping";

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _appVersion;
    private readonly string? _statePath;

    public UsagePingService(HttpMessageHandler? handler = null, string? endpoint = null,
                            string? appVersion = null, string? statePath = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _endpoint = endpoint ?? EndpointUrl;
        _appVersion = appVersion ?? AppInfo.Version;
        _statePath = statePath;
    }

    /// <summary>SessionState.Transport carries ILinkProvider.Name ("USB" / "WiFi"). Lowercasing
    /// here keeps the wire format stable if a provider's display name is ever reworded, and
    /// anything unrecognised still pings (as "unknown") rather than going silent.</summary>
    public static string NormalizeTransport(string? transport) => transport?.ToLowerInvariant() switch
    {
        "usb" => "usb",
        "wifi" => "wifi",
        _ => "unknown",
    };

    /// <summary>The worker rejects blank or oversized `fw` values with 400, and a rejected ping
    /// is never retried into a recorded day — so a device whose firmware read comes back null or
    /// empty (CompatibilityChecker builds it as `... ?? ""`, and DeviceSession still reports
    /// Connected: true) would otherwise make that install permanently invisible. Map a blank
    /// value to "unknown" and cap length at the worker's 20-character limit instead.</summary>
    private static string SanitizeFirmware(string firmware) =>
        string.IsNullOrWhiteSpace(firmware) ? "unknown"
        : firmware.Length > 20 ? firmware[..20]
        : firmware;

    public async Task PingAsync(string firmware, string? transport, CancellationToken ct = default)
    {
        // Dev builds never ping: the author connects the pedal many times a day and would
        // otherwise dominate the active-user count. Same rule as UpdateCheckService.IsNewer.
        if (_appVersion.Contains('-')) return;

        try
        {
            var state = UsageState.Load(_statePath);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (!state.ShouldPing(today)) return;

            var resp = await _http.PostAsJsonAsync(_endpoint, new
            {
                installId = state.InstallId,
                appVersion = _appVersion,
                fw = SanitizeFirmware(firmware),
                transport = NormalizeTransport(transport),
            }, ct);

            // Only a confirmed success burns the day, so an offline week doesn't silently
            // consume its days. The cost is one wasted request per launch while offline.
            if (resp.IsSuccessStatusCode)
                (state with { LastPingUtc = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) })
                    .Save(_statePath);
        }
        catch
        {
            // Offline, DNS failure, timeout, cancellation, malformed response: all no-ops.
            // Telemetry must never be able to surface an error to the user.
        }
    }
}
