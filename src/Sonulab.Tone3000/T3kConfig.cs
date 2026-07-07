using System.Text.Json;

namespace Sonulab.Tone3000;

/// <summary>App-side Tone3000 configuration. Deliberately has NO secret-key member:
/// the t3k_cs_ credential is server-only and must be impossible to reach from app code
/// (spec §4). The dev probe tool parses the raw JSON itself.</summary>
public sealed record T3kConfig(string PublishableKey, int RedirectPort)
{
    public const string DefaultBaseUrl = "https://www.tone3000.com";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "StompStationManager", "tone3000.json");

    /// <summary>Null on missing file, unparseable JSON, or a missing/placeholder key —
    /// the UI turns null into its "add your Tone3000 keys" card.</summary>
    public static T3kConfig? TryLoad(string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? DefaultPath));
            var key = doc.RootElement.TryGetProperty("publishable_key", out var k) ? k.GetString() : null;
            int port = doc.RootElement.TryGetProperty("redirect_port", out var p) && p.TryGetInt32(out var v) ? v : 0;
            if (string.IsNullOrWhiteSpace(key) || key.Contains("YOUR_KEY_HERE")) return null;
            return new T3kConfig(key, port);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }
}
