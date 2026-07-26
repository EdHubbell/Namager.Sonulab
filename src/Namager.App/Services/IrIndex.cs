using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Namager.App.Services;

/// <summary>One IR's Tone3000 identity, keyed by the SHA-256 of its 4096-byte pedal blob.</summary>
public sealed record IrIndexEntry(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("toneId")] long ToneId,
    [property: JsonPropertyName("modelId")] long ModelId,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>Maps IR blob content to the Tone3000 tone/model it came from.
///
/// A 4096-byte IR blob has no room for embedded metadata the way a 12288-byte vxamp does, so its
/// identity has to live on the PC instead.
///
/// Keyed by content rather than slot because slots move — NAMager reorders IR slots itself, and
/// VoidX can overwrite them. Content is the only stable handle.
///
/// The hash is a lookup key, not an identifier that travels: it is computed and consumed on the
/// same machine, and never sent anywhere. See PRIVACY.md.
///
/// Every failure mode (missing, corrupt, unknown schema, unwritable) degrades to "empty" rather
/// than throwing — a bad index file must never stop the app or an upload.</summary>
public sealed class IrIndex
{
    public const int Schema = 1;

    private readonly Dictionary<string, IrIndexEntry> _bySha;

    private IrIndex(IEnumerable<IrIndexEntry> entries) =>
        _bySha = entries.ToDictionary(e => e.Sha, StringComparer.Ordinal);

    public IReadOnlyCollection<IrIndexEntry> Entries => _bySha.Values;

    /// <summary>%APPDATA%\Namager\ir-index.json — the same directory as usage.json and
    /// settings.json. Guarded like AppSettingsStore.DefaultPath: a throwing folder lookup must
    /// not poison the type initializer.</summary>
    public static string DefaultPath
    {
        get
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Namager", "ir-index.json");
            }
            catch { return "ir-index.json"; }
        }
    }

    public static string ShaOf(ReadOnlySpan<byte> blob) =>
        Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

    public IrIndexEntry? Lookup(string sha) =>
        _bySha.TryGetValue(sha, out var e) ? e : null;

    /// <summary>Returns a new index with <paramref name="entry"/> added or replacing any entry
    /// with the same sha. Does not write to disk — call Save.</summary>
    public IrIndex Record(IrIndexEntry entry)
    {
        var next = new Dictionary<string, IrIndexEntry>(_bySha, StringComparer.Ordinal)
        {
            [entry.Sha] = entry,
        };
        return new IrIndex(next.Values);
    }

    public static IrIndex Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new IrIndex([]);

            var doc = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(file));
            // A file from a future writer is not safely readable — treat it as empty rather than
            // guessing at a shape we don't know.
            if (doc is null || doc.Schema != Schema || doc.Entries is null) return new IrIndex([]);

            return new IrIndex(doc.Entries.Where(e => !string.IsNullOrEmpty(e.Sha)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return new IrIndex([]);
        }
    }

    public void Save(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(file, JsonSerializer.Serialize(
                new IndexFile(Schema, [.. _bySha.Values]),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // Losing the index costs a title in the UI, never an upload or a user's data.
        }
    }

    private sealed record IndexFile(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("entries")] IrIndexEntry[]? Entries);
}
