// The DPAPI token store (T3kTokenStore) makes this library Windows-only, matching the app.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// Config-resolution internals (the current-dir → legacy-dir → embedded-key chain) are tested
// directly rather than through the real %APPDATA% locations.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Namager.Tone3000.Tests")]
