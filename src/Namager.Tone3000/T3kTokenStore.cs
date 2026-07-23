using System.Security.Cryptography;
using System.Text;

namespace Namager.Tone3000;

/// <summary>Persists the OAuth refresh token encrypted with Windows DPAPI (CurrentUser
/// scope): survives restarts, unreadable by other accounts, deleted on sign-out.
/// (The Windows-only constraint this DPAPI use imposes is declared assembly-wide in
/// AssemblyInfo.cs.)
/// <para>Reads fall back to the pre-rename config dir (commit 8257b81) so the move does not
/// silently sign out an install that was signed in; writes only ever go to the current path,
/// and <see cref="Clear"/> removes both so sign-out cannot be undone by the fallback.</para></summary>
public sealed class T3kTokenStore
{
    private readonly string _path;
    private readonly string? _legacyPath;

    /// <param name="path">Token file; defaults to %APPDATA%\Namager\tone3000.token.</param>
    /// <param name="legacyPath">Read-only fallback. Defaults to the pre-rename location only
    /// when <paramref name="path"/> is defaulted too — an explicit path means "this file".</param>
    public T3kTokenStore(string? path = null, string? legacyPath = null)
    {
        _path = path ?? TokenPath("Namager");
        _legacyPath = legacyPath ?? (path is null ? TokenPath("StompStationManager") : null);
    }

    private static string TokenPath(string dir) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     dir, "tone3000.token");

    public void Save(string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken),
                                           optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, cipher);
    }

    public string? Load() =>
        Read(_path) ?? (_legacyPath is null ? null : Read(_legacyPath));

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path),
                                                optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or FormatException
                                       or UnauthorizedAccessException)
        { return null; }                                     // corrupt/foreign/unreadable file = signed out
    }

    public void Clear()
    {
        Delete(_path);
        if (_legacyPath is not null) Delete(_legacyPath);
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* already gone / locked / ACL-broken: best effort */ }
    }
}
