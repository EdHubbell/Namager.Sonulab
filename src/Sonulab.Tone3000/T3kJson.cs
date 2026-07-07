using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sonulab.Tone3000;

// ALL wire contracts live in this one file (spec §1: the API is v1-unstable; lenient
// parsing everywhere - unknown fields ignored, missing optionals null). Field mapping
// verified against live responses by tools/T3kProbe; see docs/tone3000-api-findings.md.

public sealed record T3kUser(long Id, string? Username);

public sealed record T3kTone(
    long Id, string? Title, string? Author, string? Gear, string? Description,
    string? ImageUrl, string? PageUrl, int? Downloads, int? Stars, string? Format);

public sealed record T3kModel(long Id, string? Name, string? Format, string? ModelUrl);

public sealed record T3kPage<T>(
    IReadOnlyList<T> Data, int Page, int PageSize, int Total, int TotalPages)
{
    public static readonly T3kPage<T> Empty = new(Array.Empty<T>(), 1, 0, 0, 0);
}

public static class T3kJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
    }

    public static T3kPage<T> ParsePage<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T3kPage<T>>(json, Options) ?? T3kPage<T>.Empty; }
        catch (JsonException) { return T3kPage<T>.Empty; }
    }
}
