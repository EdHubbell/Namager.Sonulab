using System.Collections.Generic;
using System.Linq;

namespace Namager.App.Services;

/// <summary>Pure filename ↔ preset-name rules for single-preset download/upload and for
/// backup/restore. Mirrors BackupService's "NN - Name.pst" convention — including its 0-BASED
/// slot number (the UI's DisplaySlot is Index + 1) — so files move freely between a full backup
/// folder and a single-preset download. No I/O, no device access.</summary>
public static class PresetFileNaming
{
    /// <summary>Device limit: preset names are capped at 31 characters.</summary>
    public const int NameMaxChars = 31;

    /// <summary>"07 - Clean Verb.pst" -> "Clean Verb". A stem without the exact "NN - " prefix is
    /// used as-is (guessing would mangle a legitimately-named file). Trimmed and capped.</summary>
    public static string NameFromFile(string path)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (stem.Length >= 5 && char.IsAsciiDigit(stem[0]) && char.IsAsciiDigit(stem[1])
            && stem.AsSpan(2, 3).SequenceEqual(" - "))
            stem = stem[5..];
        stem = stem.Trim();
        return stem.Length > NameMaxChars ? stem[..NameMaxChars].TrimEnd() : stem;
    }

    /// <summary>"NN - Name.pst" for a download, byte-identical to what
    /// BackupService.SnapshotAllAsync writes. <paramref name="index"/> is the 0-based slot.</summary>
    public static string FileNameFor(int index, string name) => $"{index:D2} - {Sanitize(name)}.pst";

    /// <summary>The lowest free "{base} #n" (n from 2) when <paramref name="desired"/> is taken,
    /// with the base truncated so the whole name stays within <see cref="NameMaxChars"/>.
    /// Case-insensitive: the device matches preset names when saving, so two names differing only
    /// in case would be a trap.</summary>
    public static string ResolveUnique(string desired, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(
            existing.Where(n => !string.IsNullOrEmpty(n)), System.StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired)) return desired;

        for (int n = 2; n < 1000; n++)
        {
            var suffix = $" #{n}";
            var head = desired.Length + suffix.Length > NameMaxChars
                ? desired[..(NameMaxChars - suffix.Length)].TrimEnd()
                : desired;
            var candidate = head + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new System.InvalidOperationException($"No free name based on '{desired}'.");
    }

    /// <summary>Inverse of <see cref="FileNameFor"/>: "07 - Clean Verb.pst" -> (7, "Clean Verb").
    /// Null when the name carries no usable "NN - " slot prefix or names a slot that does not
    /// exist on the device.</summary>
    public static (int Index, string Name)? ParseBackupFileName(string path)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (stem.Length < 5 || !char.IsAsciiDigit(stem[0]) || !char.IsAsciiDigit(stem[1])
            || !stem.AsSpan(2, 3).SequenceEqual(" - ")) return null;

        int index = (stem[0] - '0') * 10 + (stem[1] - '0');
        if (index < 0 || index >= Sonulab.Core.Services.DeviceRepository.SlotCount) return null;

        var name = stem[5..].Trim();
        if (name.Length == 0) return null;
        return (index, name.Length > NameMaxChars ? name[..NameMaxChars].TrimEnd() : name);
    }

    /// <summary>Sort a folder's .pst files into what restore will write (slot-ascending) and what
    /// it will skip. First file wins for a duplicated slot number, so the outcome is deterministic
    /// regardless of directory-enumeration order.</summary>
    public static RestorePlan PlanRestore(IEnumerable<string> pstPaths)
    {
        var items = new List<RestoreItem>();
        var skipped = new List<string>();
        foreach (var p in pstPaths.OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase))
        {
            if (ParseBackupFileName(p) is { } hit && !items.Any(i => i.Index == hit.Index))
                items.Add(new RestoreItem(hit.Index, hit.Name, p));
            else
                skipped.Add(System.IO.Path.GetFileName(p));
        }
        return new RestorePlan(items.OrderBy(i => i.Index).ToList(), skipped);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

/// <summary>One file a restore will write, and where it goes.</summary>
public sealed record RestoreItem(int Index, string Name, string Path);

/// <summary>What a restore run will do: the files it can place, and the file NAMES it will not
/// touch. Skipped files are reported rather than guessed at — writing a preset to a slot the user
/// did not ask for is worse than skipping it.</summary>
public sealed record RestorePlan(IReadOnlyList<RestoreItem> Items, IReadOnlyList<string> Skipped);
