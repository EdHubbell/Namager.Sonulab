namespace Namager.Tone3000;

public enum T3kQueryKind { Text, ToneId, BadLink }

/// <summary>Classification of the search box text: a text search, a direct tone id, or
/// something that looks like a link but isn't a usable Tone3000 tone reference.</summary>
public readonly record struct T3kQuery(T3kQueryKind Kind, long ToneId, string? Text);

/// <summary>Pure classifier for the Tone3000 search box. No HTTP, no state.
/// Recognizes a tone3000.com tone URL or a bare numeric id so the UI can jump straight
/// to that tone (the API text search lags the website — docs/tone3000-api-findings.md).</summary>
public static class T3kSearchQuery
{
    private const int MaxIdDigits = 18;   // stays inside a long
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    public static T3kQuery Parse(string? input)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return new(T3kQueryKind.Text, 0, null);

        // 1. Bare numeric id.
        if (IsToneNumber(s, out var bareId)) return new(T3kQueryKind.ToneId, bareId, null);

        // 2. Looks like a link? (explicit scheme, or a scheme-less tone3000.com paste)
        bool looksLink = s.StartsWith("http://", Ci) || s.StartsWith("https://", Ci)
                         || s.Contains("tone3000.com/", Ci);
        if (!looksLink) return new(T3kQueryKind.Text, 0, s);

        var withScheme = s.StartsWith("http://", Ci) || s.StartsWith("https://", Ci)
                         ? s : "https://" + s;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            return new(T3kQueryKind.BadLink, 0, null);

        if (!uri.Host.Equals("tone3000.com", Ci) && !uri.Host.Equals("www.tone3000.com", Ci))
            return new(T3kQueryKind.BadLink, 0, null);

        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2 || !segs[0].Equals("tones", Ci))
            return new(T3kQueryKind.BadLink, 0, null);

        var slug = segs[1];
        var tail = slug.Contains('-') ? slug[(slug.LastIndexOf('-') + 1)..] : slug;
        return IsToneNumber(tail, out var id)
            ? new(T3kQueryKind.ToneId, id, null)
            : new(T3kQueryKind.BadLink, 0, null);
    }

    private static bool IsToneNumber(string s, out long id)
    {
        id = 0;
        if (s.Length is 0 or > MaxIdDigits) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return long.TryParse(s, out id);
    }
}
