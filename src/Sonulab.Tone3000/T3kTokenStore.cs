using System.Security.Cryptography;
using System.Text;

namespace Sonulab.Tone3000;

/// <summary>Persists the OAuth refresh token encrypted with Windows DPAPI (CurrentUser
/// scope): survives restarts, unreadable by other accounts, deleted on sign-out.
/// (The Windows-only constraint this DPAPI use imposes is declared assembly-wide in
/// AssemblyInfo.cs.)</summary>
public sealed class T3kTokenStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StompStationManager", "tone3000.token");

    public void Save(string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken),
                                           optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, cipher);
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path),
                                                optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or FormatException
                                       or UnauthorizedAccessException)
        { return null; }                                     // corrupt/foreign/unreadable file = signed out
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* already gone / locked / ACL-broken: best effort */ }
    }
}
