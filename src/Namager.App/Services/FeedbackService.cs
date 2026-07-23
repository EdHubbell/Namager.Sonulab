using System.Net.Http;
using System.Net.Http.Json;

namespace Namager.App.Services;

/// <summary>One user feedback submission; becomes a public GitHub issue via the
/// feedback worker (infra/feedback-worker).</summary>
public sealed record FeedbackReport(string Name, string Email, string Message, string AppVersion, string Os);

public interface IFeedbackService
{
    /// <summary>Throws <see cref="FeedbackSendException"/> on any delivery failure.</summary>
    Task SendAsync(FeedbackReport report, CancellationToken ct = default);
}

public sealed class FeedbackSendException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Delivers feedback to the Cloudflare Worker (infra/feedback-worker), which
/// creates the GitHub issue. The endpoint URL is public knowledge, not a secret.</summary>
public sealed class FeedbackService : IFeedbackService
{
    /// <summary>Deployed worker endpoint. If `wrangler deploy` reports a different URL
    /// (workers.dev subdomain is per-account), update this constant to match.</summary>
    public const string EndpointUrl = "https://namager-sonulab-feedback.ed-eed.workers.dev/";


    private readonly HttpClient _http;
    private readonly string _endpoint;

    public FeedbackService(HttpMessageHandler? handler = null, string? endpoint = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _endpoint = endpoint ?? EndpointUrl;
    }

    public async Task SendAsync(FeedbackReport report, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(_endpoint, new
            {
                name = report.Name,
                email = report.Email,
                message = report.Message,
                appVersion = report.AppVersion,
                os = report.Os,
                website = "",                    // honeypot: real clients always send empty
            }, ct);
            if (!resp.IsSuccessStatusCode)
                throw new FeedbackSendException($"feedback endpoint returned {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FeedbackSendException("feedback endpoint unreachable", ex);
        }
    }
}
