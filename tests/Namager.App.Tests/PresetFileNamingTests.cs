using System.Linq;
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

    [Fact] public void ParseBackupFileName_reads_the_zero_based_slot_and_name()
    {
        var hit = PresetFileNaming.ParseBackupFileName(@"C:\b\07 - Clean Verb.pst");
        Assert.NotNull(hit);
        Assert.Equal(7, hit!.Value.Index);
        Assert.Equal("Clean Verb", hit.Value.Name);
    }

    [Theory]
    [InlineData(@"C:\b\Clean Verb.pst")]      // no slot prefix
    [InlineData(@"C:\b\30 - Too High.pst")]   // beyond the 30 slots
    [InlineData(@"C:\b\99 - Way Off.pst")]
    [InlineData(@"C:\b\07 - .pst")]           // no name
    public void ParseBackupFileName_rejects_names_it_cannot_place(string path)
        => Assert.Null(PresetFileNaming.ParseBackupFileName(path));

    [Fact] public void PlanRestore_sorts_by_slot_and_reports_what_it_skipped()
    {
        var plan = PresetFileNaming.PlanRestore(new[]
        {
            @"C:\b\05 - Five.pst",
            @"C:\b\01 - One.pst",
            @"C:\b\notes.pst",
        });
        Assert.Equal(new[] { 1, 5 }, plan.Items.Select(i => i.Index));
        Assert.Equal(new[] { "notes.pst" }, plan.Skipped);
    }

    [Fact] public void PlanRestore_keeps_the_first_file_for_a_duplicated_slot_and_skips_the_rest()
    {
        var plan = PresetFileNaming.PlanRestore(new[]
        {
            @"C:\b\03 - A.pst",
            @"C:\b\03 - B.pst",
        });
        Assert.Equal("A", Assert.Single(plan.Items).Name);
        Assert.Equal(new[] { "03 - B.pst" }, plan.Skipped);
    }

    // Same two files, input order reversed: proves the outcome is decided by PlanRestore's own
    // sort, not by the enumeration order it happened to receive. Without this, a "first file wins"
    // test using already-alphabetical input would pass equally against an implementation that does
    // no sorting at all.
    [Fact] public void PlanRestore_duplicate_slot_outcome_is_independent_of_input_order()
    {
        var plan = PresetFileNaming.PlanRestore(new[]
        {
            @"C:\b\03 - B.pst",
            @"C:\b\03 - A.pst",
        });
        Assert.Equal("A", Assert.Single(plan.Items).Name);
        Assert.Equal(new[] { "03 - B.pst" }, plan.Skipped);
    }
}
