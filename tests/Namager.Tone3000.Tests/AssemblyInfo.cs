// The library under test is Windows-only (DPAPI token store) — declare the same platform
// here so exercising it doesn't raise CA1416 in the test assembly.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
