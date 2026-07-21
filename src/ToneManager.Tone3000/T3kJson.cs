using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToneManager.Tone3000;

// ALL wire contracts live in this one file (spec §1: the API is v1-unstable; lenient
// parsing everywhere - unknown fields ignored, missing optionals null). Field mapping
// verified against live responses by tools/T3kProbe; see docs/tone3000-api-findings.md.

// GET /api/v1/user returns a UUID string id ("ca1c3703-e575-…"), not an integer - unlike
// tone/model ids. Verified live 2026-07-07; see docs/tone3000-api-findings.md.
public sealed record T3kUser(string? Id, string? Username);

// Nested author info: the API has NO top-level "author" field on a tone - the username
// lives under a nested "user" object. Verified live 2026-07-07.
public sealed record T3kToneAuthor(string? Username);

public sealed record T3kTone(
    long Id, string? Title, string? Gear, string? Description,
    IReadOnlyList<string>? Images,
    [property: JsonPropertyName("url")] string? PageUrl,
    [property: JsonPropertyName("downloads_count")] int? Downloads,
    [property: JsonPropertyName("favorites_count")] int? Stars,
    string? Format,
    T3kToneAuthor? User)
{
    /// <summary>Card art: the API returns an "images" array (0 or more); this is the first
    /// entry, or null. Verified live 2026-07-07 - assumption of a singular "image_url" was wrong.</summary>
    public string? ImageUrl => Images is { Count: > 0 } ? Images[0] : null;

    /// <summary>Convenience accessor over the nested "user.username" - there is no top-level
    /// "author" field (see <see cref="T3kToneAuthor"/>).</summary>
    public string? Author => User?.Username;
}

// NOTE: /api/v1/models?tone_id= does NOT return a per-model "format" field (verified live
// 2026-07-07) - only the parent T3kTone has "format". Callers needing an extension should
// derive it from ModelUrl's file extension (e.g. "….nam") or the parent tone's Format.
// Field kept (always null today) for forward compatibility if the API adds it later.
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
