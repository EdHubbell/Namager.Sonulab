using Namager.App.Services;
using Xunit;

public class AppPathsTests
{
    [Fact] public void BackupRoot_is_under_Documents()
    {
        var docs = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        Assert.StartsWith(docs, AppPaths.BackupRoot);
        Assert.EndsWith("NAMager Backups", AppPaths.BackupRoot);
    }

    /// <summary>The whole point of this helper: the amp/IR slot archive used to go to a RELATIVE
    /// "docs\backups", which resolves against the process working directory and put the files
    /// somewhere no user would look in an installed build.</summary>
    [Fact] public void SlotBackups_is_absolute_and_under_the_backup_root()
    {
        Assert.True(System.IO.Path.IsPathRooted(AppPaths.SlotBackups));
        Assert.StartsWith(AppPaths.BackupRoot, AppPaths.SlotBackups);
        Assert.EndsWith("Replaced Slots", AppPaths.SlotBackups);
    }

    [Fact] public void Paths_are_computed_on_demand_and_never_throw()
    {
        // A getter, not a static initializer: a throwing type initializer would poison every
        // later call, including the ones the connect handler makes.
        Assert.Equal(AppPaths.BackupRoot, AppPaths.BackupRoot);
        Assert.Equal(AppPaths.SlotBackups, AppPaths.SlotBackups);
    }
}
