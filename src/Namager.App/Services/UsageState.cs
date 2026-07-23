using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namager.App.Services;

/// <summary>Local state behind the anonymous usage ping: a random install ID and the last day
/// we pinged. Lives next to tone3000.json. Deleting the file resets the install identity —
/// that is the user's documented escape hatch (PRIVACY.md).
/// Every failure mode (missing, corrupt, unreadable, unwritable) degrades to "first run"
/// rather than throwing: telemetry must never be able to break the app.</summary>
public sealed record UsageState(
    [property: JsonPropertyName("installId")] string InstallId,
    [property: JsonPropertyName("lastPingUtc")] string? LastPingUtc)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Namager", "usage.json");

    /// <summary>Never throws. A missing or unusable file yields a fresh install ID, which is
    /// only persisted once <see cref="Save"/> is called after a successful ping.</summary>
    public static UsageState Load(string? path = null)
    {
        try
        {
            var state = JsonSerializer.Deserialize<UsageState>(File.ReadAllText(path ?? DefaultPath));
            if (state is not null && Guid.TryParse(state.InstallId, out _)) return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or NotSupportedException)
        { /* fall through to a fresh identity */ }

        return new UsageState(Guid.NewGuid().ToString(), null);
    }

    /// <summary>True when we have not yet pinged on this UTC day. The date is passed in so the
    /// gate is testable without a clock.</summary>
    public bool ShouldPing(DateOnly todayUtc)
        => LastPingUtc != todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Never throws. A failed write just means the day gate doesn't stick — the app
    /// pings again next launch, which is harmless (the worker de-duplicates by day anyway).</summary>
    public void Save(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        { /* best effort */ }
    }
}
