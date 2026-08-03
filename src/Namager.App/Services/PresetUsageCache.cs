using System.Text.Json;
using System.Text.Json.Serialization;
using Sonulab.Core.Services;

namespace Namager.App.Services;

/// <summary>Per-device disk cache of the preset→amp/IR usage map, so a reconnect can show
/// highlights instantly (warm start) while the background scan revalidates.
///
/// Keyed by root\sys\_id because the map belongs to a pedal, not to this PC. Entries are
/// per-slot rows; on load the scanner keeps only rows whose slot still holds a preset of the
/// same name, so renames/deletes/reorders done outside the app drop out cheaply. An IN-PLACE
/// edit outside the app is undetectable until the scan reaches that slot — a provisional
/// highlight may be stale for up to one scan (~15-30 s). Guards never trust cached data
/// (PresetUsageService.IsComplete stays false until a real scan finishes).
///
/// Names stay local: this file is never transmitted. See PRIVACY.md.
///
/// Every failure mode (missing, corrupt, unknown schema, unwritable) degrades to "empty" /
/// no-op rather than throwing — losing the cache costs a warm start, never data.</summary>
public sealed class PresetUsageCache
{
    public const int Schema = 1;

    /// <summary>Devices kept, newest savedUtc first. 8 comfortably covers a multi-pedal bench
    /// without letting the file grow unbounded.</summary>
    public const int MaxDevices = 8;

    private readonly List<DeviceEntry> _devices;   // invariant: unique ids

    private PresetUsageCache(List<DeviceEntry> devices) => _devices = devices;

    /// <summary>%APPDATA%\Namager\preset-usage-cache.json — same directory as settings.json /
    /// ir-index.json. Guarded like IrIndex.DefaultPath: a throwing folder lookup must not
    /// poison the type initializer.</summary>
    public static string DefaultPath
    {
        get
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Namager", "preset-usage-cache.json");
            }
            catch { return "preset-usage-cache.json"; }
        }
    }

    public IReadOnlyList<SlotUsage> SlotsFor(string deviceId) =>
        _devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            ?.ToSlotUsages() ?? Array.Empty<SlotUsage>();

    /// <summary>Returns a new cache with <paramref name="deviceId"/>'s rows replaced and its
    /// savedUtc stamped now; devices beyond <see cref="MaxDevices"/> are pruned oldest-first.
    /// Ties in savedUtc (e.g. several WithDevice calls in the same tick) are broken explicitly
    /// by insertion order — the earliest-inserted of a tied group is pruned first — rather than
    /// relying on however a stable sort happens to interact with Take. Does not write to disk —
    /// call Save.</summary>
    public PresetUsageCache WithDevice(string deviceId, IReadOnlyList<SlotUsage> slots)
    {
        var candidates = _devices
            .Where(d => !string.Equals(d.Id, deviceId, StringComparison.Ordinal))
            .Append(DeviceEntry.From(deviceId, DateTime.UtcNow, slots))
            .ToList();
        var next = candidates
            .Select((d, i) => (Device: d, Pos: i))
            .OrderByDescending(x => x.Device.SavedUtc)
            .ThenByDescending(x => x.Pos)          // tie: later position = newer insertion, keep it
            .Take(MaxDevices)
            .OrderBy(x => x.Pos)                   // restore insertion order for stable file output
            .Select(x => x.Device)
            .ToList();
        return new PresetUsageCache(next);
    }

    public static PresetUsageCache Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return new PresetUsageCache([]);

            var doc = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file));
            // A file from a future writer is not safely readable — empty beats guessing.
            if (doc is null || doc.Schema != Schema || doc.Devices is null)
                return new PresetUsageCache([]);

            var devices = doc.Devices
                .Where(d => d is not null && !string.IsNullOrEmpty(d.Id))
                .GroupBy(d => d!.Id, StringComparer.Ordinal)
                .Select(g => g.First()!.Sanitized())
                .ToList();
            return new PresetUsageCache(devices);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return new PresetUsageCache([]);
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
                new CacheFile(Schema, [.. _devices]),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // Losing the cache costs a warm start, never data.
        }
    }

    private sealed record CacheFile(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("devices")] DeviceEntry[]? Devices);

    private sealed record DeviceEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("savedUtc")] DateTime SavedUtc,
        [property: JsonPropertyName("slots")] SlotDto[]? Slots)
    {
        public static DeviceEntry From(string id, DateTime savedUtc, IReadOnlyList<SlotUsage> slots) =>
            new(id, savedUtc,
                slots.Select(s => new SlotDto(s.Index, s.PresetName, s.Amp, s.Irs.ToArray())).ToArray());

        /// <summary>Drop rows a well-behaved writer would never produce (slot out of range,
        /// blank preset name); normalize null irs to empty.</summary>
        public DeviceEntry Sanitized() =>
            this with
            {
                Slots = (Slots ?? []).Where(s =>
                    s is not null && s.Slot is >= 0 and < 30 && !string.IsNullOrEmpty(s.Preset))
                    .ToArray(),
            };

        public IReadOnlyList<SlotUsage> ToSlotUsages() =>
            (Slots ?? []).Select(s => new SlotUsage(
                s.Slot, s.Preset, s.Amp,
                s.Irs is null ? Array.Empty<string>() : s.Irs)).ToList();
    }

    private sealed record SlotDto(
        [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("preset")] string Preset,
        [property: JsonPropertyName("amp")] string? Amp,
        [property: JsonPropertyName("irs")] string[]? Irs);
}
