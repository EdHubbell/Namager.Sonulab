using Namager.App.Services;

namespace Namager.App.Tests;

public class IrIndexTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ir-index-{Guid.NewGuid():N}.json");

    [Fact]
    public void ShaOf_is_lowercase_hex_and_stable()
    {
        var blob = new byte[4096];
        blob[0] = 0xAB;
        var a = IrIndex.ShaOf(blob);
        var b = IrIndex.ShaOf(blob);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Equal(a.ToLowerInvariant(), a);
    }

    [Fact]
    public void ShaOf_differs_for_different_content()
    {
        var one = new byte[4096];
        var two = new byte[4096];
        two[4095] = 1;

        Assert.NotEqual(IrIndex.ShaOf(one), IrIndex.ShaOf(two));
    }

    [Fact]
    public void Record_then_Lookup_round_trips_through_a_file()
    {
        var path = TempPath();
        try
        {
            var entry = new IrIndexEntry("abc123", ToneId: 2468, ModelId: 1357, Title: "4x12 Greenback");
            IrIndex.Load(path).Record(entry).Save(path);

            var found = IrIndex.Load(path).Lookup("abc123");

            Assert.NotNull(found);
            Assert.Equal(2468, found!.ToneId);
            Assert.Equal(1357, found.ModelId);
            Assert.Equal("4x12 Greenback", found.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Lookup_returns_null_for_unknown_content()
    {
        Assert.Null(IrIndex.Load(TempPath()).Lookup("not-in-there"));
    }

    [Fact]
    public void Record_replaces_an_entry_with_the_same_sha()
    {
        var path = TempPath();
        try
        {
            IrIndex.Load(path)
                   .Record(new IrIndexEntry("dup", 1, 1, "old"))
                   .Record(new IrIndexEntry("dup", 2, 2, "new"))
                   .Save(path);

            var index = IrIndex.Load(path);

            Assert.Equal(1, index.Entries.Count);
            Assert.Equal("new", index.Lookup("dup")!.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_loads_as_empty_and_does_not_throw()
    {
        var index = IrIndex.Load(TempPath());
        Assert.Empty(index.Entries);
    }

    [Fact]
    public void Corrupt_file_loads_as_empty_and_does_not_throw()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Empty(IrIndex.Load(path).Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_schema_version_loads_as_empty_rather_than_guessing()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """{"schema":999,"entries":[{"sha":"x","toneId":1,"modelId":2}]}""");
            Assert.Empty(IrIndex.Load(path).Entries);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_to_an_unwritable_path_does_not_throw()
    {
        var index = IrIndex.Load(TempPath()).Record(new IrIndexEntry("x", 1, 2, null));
        // A directory path is never a writable file target.
        index.Save(Path.GetTempPath());
    }
}
