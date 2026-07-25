using Namager.App.Services;
using Xunit;

public class PresetFileNamingTests
{
    [Theory]
    [InlineData(@"C:\b\07 - Clean Verb.pst", "Clean Verb")]
    [InlineData(@"C:\b\00 - First.pst", "First")]
    [InlineData(@"C:\b\Clean Verb.pst", "Clean Verb")]
    [InlineData(@"C:\b\7 - Odd.pst", "7 - Odd")]          // single digit: not the backup convention
    [InlineData(@"C:\b\07-NoSpaces.pst", "07-NoSpaces")]  // missing " - ": left alone
    public void NameFromFile_strips_the_backup_slot_prefix(string path, string expected)
        => Assert.Equal(expected, PresetFileNaming.NameFromFile(path));

    [Fact] public void NameFromFile_caps_at_the_device_limit()
    {
        var name = PresetFileNaming.NameFromFile(@"C:\b\" + new string('x', 60) + ".pst");
        Assert.Equal(31, name.Length);
    }

    [Fact] public void FileNameFor_matches_the_BackupService_convention_and_is_zero_based()
        => Assert.Equal("07 - Clean Verb.pst", PresetFileNaming.FileNameFor(7, "Clean Verb"));

    [Fact] public void FileNameFor_sanitises_characters_illegal_in_a_file_name()
        => Assert.Equal("03 - A_B.pst", PresetFileNaming.FileNameFor(3, "A/B"));

    [Fact] public void ResolveUnique_leaves_a_free_name_alone()
        => Assert.Equal("Clean", PresetFileNaming.ResolveUnique("Clean", new[] { "Dirty", "" }));

    [Fact] public void ResolveUnique_appends_the_lowest_free_number()
    {
        Assert.Equal("Clean #2", PresetFileNaming.ResolveUnique("Clean", new[] { "Clean" }));
        Assert.Equal("Clean #3", PresetFileNaming.ResolveUnique("Clean", new[] { "Clean", "Clean #2" }));
    }

    [Fact] public void ResolveUnique_is_case_insensitive()
        => Assert.Equal("Clean #2", PresetFileNaming.ResolveUnique("Clean", new[] { "CLEAN" }));

    [Fact] public void ResolveUnique_truncates_the_base_to_stay_within_the_device_limit()
    {
        var longName = new string('x', 31);
        var result = PresetFileNaming.ResolveUnique(longName, new[] { longName });
        Assert.True(result.Length <= 31);
        Assert.EndsWith(" #2", result);
    }
}
