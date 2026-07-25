namespace Namager.App.Services;

/// <summary>Absolute, user-visible locations the app writes to. Computed on demand inside a guard
/// rather than in a static initializer: a throwing type initializer would poison every later call
/// to this class, and these are read during the connect handler.</summary>
public static class AppPaths
{
    /// <summary>Documents\NAMager Backups — the root the preset backup folders live under.</summary>
    public static string BackupRoot
    {
        get
        {
            try
            {
                return System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
                    "NAMager Backups");
            }
            catch { return "NAMager Backups"; }
        }
    }

    /// <summary>Where an amp or IR slot's contents are archived before the slot is wiped
    /// (<c>SlotBlobService.DeleteAsync</c> backs the slot up first). Absolute and under Documents:
    /// the old value was a RELATIVE "docs\backups", which resolves against the process working
    /// directory, so in an installed build these landed somewhere the user would never find and the
    /// delete tooltip's "backs up first" looked like a lie.</summary>
    public static string DeletedSlotBackups => System.IO.Path.Combine(BackupRoot, "Deleted Slots");
}
