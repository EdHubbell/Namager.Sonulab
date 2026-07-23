using System.Text.Json;

namespace Namager.Tone3000;

/// <summary>App-side Tone3000 configuration. Deliberately has NO secret-key member:
/// the t3k_cs_ credential is server-only and must be impossible to reach from app code
/// (spec §4). The dev probe tool parses the raw JSON itself.</summary>
public sealed record T3kConfig(string PublishableKey, int RedirectPort)
{
    public const string DefaultBaseUrl = "https://www.tone3000.com";

    /// <summary>The OAuth client_id shipped inside the build, so an installed app can offer
    /// browser sign-in with no per-machine setup. A publishable key is public by design —
    /// PKCE (see <see cref="T3kAuth"/>) is what makes the native flow safe without a secret.
    /// NEVER put a t3k_cs_ secret here; it would be extractable from every install.</summary>
    public const string EmbeddedPublishableKey = "t3k_pub_BNwKg3EurlQuNDJvuJ9LWJ4c_lfXc0mW";

    public static string DefaultPath => ConfigPath("Namager");

    /// <summary>Pre-rename config dir (commit 8257b81 moved it) — read-only fallback so an
    /// install that predates the move keeps its key instead of reverting to the default.</summary>
    internal static string LegacyPath => ConfigPath("StompStationManager");

    private static string ConfigPath(string dir) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     dir, "tone3000.json");

    /// <summary>With no <paramref name="path"/>, resolves current dir → legacy dir → the
    /// embedded key, and so is null only in a build stripped of its embedded key. With an
    /// explicit path, that file alone decides: null on missing file, unparseable JSON, or a
    /// missing/placeholder key — the UI turns null into its "add your Tone3000 keys" card.</summary>
    public static T3kConfig? TryLoad(string? path = null) =>
        path is not null ? LoadFile(path) : LoadDefault(DefaultPath, LegacyPath);

    internal static T3kConfig? LoadDefault(string primary, string legacy) =>
        LoadFile(primary) ?? LoadFile(legacy) ?? Embedded();

    private static T3kConfig? Embedded() =>
        IsUsableKey(EmbeddedPublishableKey) ? new T3kConfig(EmbeddedPublishableKey, 0) : null;

    private static T3kConfig? LoadFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var key = doc.RootElement.TryGetProperty("publishable_key", out var k) ? k.GetString() : null;
            int port = doc.RootElement.TryGetProperty("redirect_port", out var p) && p.TryGetInt32(out var v) ? v : 0;
            if (!IsUsableKey(key)) return null;
            return new T3kConfig(key!, port);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static bool IsUsableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && !key.Contains("YOUR_KEY_HERE");
}
