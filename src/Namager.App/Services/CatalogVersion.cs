namespace Namager.App.Services;

/// <summary>Monotonic "the pedal's amp/IR name lists changed" counter — one instance per
/// connection. Bumped by the amp and IR list view models after a VERIFIED mutation (delete,
/// rename, upload, reorder); read by the parameter editor, which re-reads its amp/IR picker
/// option lists when the number it last loaded with no longer matches.
///
/// Deliberately a pull, not a push: the editor only needs fresh options at the moment the user
/// lands on the Presets tab, so an event subscription would just add lifetime management for no
/// behavioural gain.</summary>
public sealed class CatalogVersion
{
    private int _version;

    public int Version => System.Threading.Volatile.Read(ref _version);

    public void Bump() => System.Threading.Interlocked.Increment(ref _version);
}
