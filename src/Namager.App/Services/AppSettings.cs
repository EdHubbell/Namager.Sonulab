using System.Text.Json;

namespace Namager.App.Services;

/// <summary>User preferences persisted between runs. Kept deliberately small — anything the app
/// can re-derive from the device or the filesystem does not belong here.</summary>
public sealed record AppSettings
{
    /// <summary>"System" (follow the OS), "Light", or "Dark". Unknown values behave as System.</summary>
    public string Theme { get; init; } = "System";
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON under %APPDATA%\Namager — the same
/// directory the Tone3000 config already uses. Every failure path returns defaults rather than
/// throwing: a bad settings file must never stop the app from starting.</summary>
public static class AppSettingsStore
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "Namager", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!System.IO.File.Exists(file)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(file))
                   ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort: a preference that fails to persist must not break the session */ }
    }
}

/// <summary>Maps the persisted theme string onto Avalonia's ThemeVariant.</summary>
public static class ThemeSettings
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static Avalonia.Styling.ThemeVariant ToVariant(string theme) => theme switch
    {
        Light => Avalonia.Styling.ThemeVariant.Light,
        Dark => Avalonia.Styling.ThemeVariant.Dark,
        _ => Avalonia.Styling.ThemeVariant.Default,     // System / unknown: follow the OS
    };
}
