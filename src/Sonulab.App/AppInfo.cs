using System.Reflection;

namespace Sonulab.App;

/// <summary>Version of the running app. CI stamps it from the git tag (-p:Version=1.2.3);
/// local builds are "1.0.0-dev". The SDK appends "+<git sha>" to the informational
/// version — stripped here.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
