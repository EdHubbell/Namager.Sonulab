using System.Net;

namespace Namager.Tone3000;

public interface IT3kClient
{
    Task<T3kPage<T3kTone>> SearchAsync(string? query, string? format, int page, CancellationToken ct = default);
    Task<T3kPage<T3kTone>> FavoritedAsync(int page, CancellationToken ct = default);
    Task<T3kPage<T3kTone>> DownloadedAsync(int page, CancellationToken ct = default);
    Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<T3kModel>> GetModelsAsync(long toneId, CancellationToken ct = default);
    Task<T3kUser?> GetUserAsync(CancellationToken ct = default);
    Task SetFavoriteAsync(long toneId, bool favorite, CancellationToken ct = default);
}

/// <summary>Typed Tone3000 API client. Every call carries the user's OAuth Bearer token;
/// HTTP failures map to T3kException kinds the UI shows verbatim. Parameter names match
/// docs/tone3000-api-findings.md (the live-probe record).
/// Register as a long-lived singleton — owns an HttpClient.</summary>
public sealed class T3kClient(IT3kAuth auth, HttpMessageHandler? handler = null, string? baseUrl = null) : IT3kClient
{
    private const int PageSize = 20;
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    { BaseAddress = new Uri(baseUrl ?? T3kConfig.DefaultBaseUrl) };

    public Task<T3kPage<T3kTone>> SearchAsync(string? query, string? format, int page, CancellationToken ct = default)
    {
        var q = new List<string> { $"page={page}", $"page_size={PageSize}" };
        if (!string.IsNullOrWhiteSpace(query)) q.Add($"query={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrWhiteSpace(format)) q.Add($"format={Uri.EscapeDataString(format)}");
        return GetPageAsync<T3kTone>($"/api/v1/tones/search?{string.Join('&', q)}", ct);
    }

    public Task<T3kPage<T3kTone>> FavoritedAsync(int page, CancellationToken ct = default) =>
        GetPageAsync<T3kTone>($"/api/v1/tones/favorited?page={page}&page_size={PageSize}", ct);

    public Task<T3kPage<T3kTone>> DownloadedAsync(int page, CancellationToken ct = default) =>
        GetPageAsync<T3kTone>($"/api/v1/tones/downloaded?page={page}&page_size={PageSize}", ct);

    public async Task<T3kTone?> GetToneAsync(long id, CancellationToken ct = default) =>
        T3kJson.Parse<T3kTone>(await GetStringAsync($"/api/v1/tones/{id}", ct));

    public async Task<IReadOnlyList<T3kModel>> GetModelsAsync(long toneId, CancellationToken ct = default)
    {
        // The server's default page size (10) silently truncates a tone's model list unless
        // we ask for a bigger page and follow total_pages for the rest (sequential, not
        // parallel, to stay a good API citizen and keep request order deterministic for tests).
        const int modelsPageSize = 100;
        var first = await GetPageAsync<T3kModel>($"/api/v1/models?tone_id={toneId}&page_size={modelsPageSize}&page=1", ct);
        if (first.TotalPages <= 1) return first.Data;

        var all = new List<T3kModel>(first.Data);
        for (int page = 2; page <= first.TotalPages; page++)
        {
            var next = await GetPageAsync<T3kModel>($"/api/v1/models?tone_id={toneId}&page_size={modelsPageSize}&page={page}", ct);
            all.AddRange(next.Data);
        }
        return all;
    }

    public async Task<T3kUser?> GetUserAsync(CancellationToken ct = default) =>
        T3kJson.Parse<T3kUser>(await GetStringAsync("/api/v1/user", ct));

    public async Task SetFavoriteAsync(long toneId, bool favorite, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(favorite ? HttpMethod.Put : HttpMethod.Delete,
                                         $"/api/v1/tones/{toneId}/favorite");
        using var resp = await SendAsync(req, ct);
    }

    private async Task<T3kPage<T>> GetPageAsync<T>(string path, CancellationToken ct) =>
        T3kJson.ParsePage<T>(await GetStringAsync(path, ct));

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.Authorization = new("Bearer", await auth.GetAccessTokenAsync(ct));
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException e)
        { throw new T3kException("Could not reach tone3000.com — check your connection.", T3kError.Network, e); }
        if (resp.IsSuccessStatusCode) return resp;
        resp.Dispose();
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new T3kException("Tone3000 rejected the request — sign in again.", T3kError.Auth),
            HttpStatusCode.TooManyRequests =>
                new T3kException("Tone3000 rate limit reached — wait a minute and retry.", T3kError.RateLimited),
            _ => new T3kException($"Tone3000 request failed (HTTP {(int)resp.StatusCode}).", T3kError.Api),
        };
    }
}
