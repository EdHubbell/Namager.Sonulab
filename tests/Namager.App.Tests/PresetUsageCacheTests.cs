using Namager.App.Services;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageCacheTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"nmgr-usage-cache-{Guid.NewGuid():N}.json");

    private static readonly SlotUsage[] SampleSlots =
    {
        new(0, "Lead", "Plexi", new[] { "Cab A" }),
        new(3, "Rhythm", null, new[] { "Cab A", "Cab B" }),
    };

    [Fact]
    public void Roundtrips_slots_per_device()
    {
        var path = TempPath();
        try
        {
            PresetUsageCache.Load(path).WithDevice("dev-1", SampleSlots).Save(path);
            var loaded = PresetUsageCache.Load(path);
            Assert.Equal(SampleSlots.Select(s => (s.Index, s.PresetName, s.Amp)),
                         loaded.SlotsFor("dev-1").Select(s => (s.Index, s.PresetName, s.Amp)));
            Assert.Equal(new[] { "Cab A", "Cab B" }, loaded.SlotsFor("dev-1")[1].Irs);
            Assert.Empty(loaded.SlotsFor("dev-2"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_corrupt_and_future_schema_files_load_as_empty()
    {
        Assert.Empty(PresetUsageCache.Load(TempPath()).SlotsFor("dev-1"));

        var corrupt = TempPath();
        var future = TempPath();
        try
        {
            File.WriteAllText(corrupt, "{ not json");
            Assert.Empty(PresetUsageCache.Load(corrupt).SlotsFor("dev-1"));

            File.WriteAllText(future, """{ "schema": 99, "devices": [] }""");
            Assert.Empty(PresetUsageCache.Load(future).SlotsFor("dev-1"));
        }
        finally { File.Delete(corrupt); File.Delete(future); }
    }

    [Fact]
    public void Save_to_unwritable_path_does_not_throw()
    {
        var cache = PresetUsageCache.Load(TempPath()).WithDevice("dev-1", SampleSlots);
        cache.Save(Path.Combine(Path.GetTempPath(), "nmgr-no-such-dir-\0-x", "cache.json"));
    }

    [Fact]
    public void WithDevice_replaces_same_id_and_preserves_others()
    {
        var cache = PresetUsageCache.Load(TempPath())
            .WithDevice("dev-1", SampleSlots)
            .WithDevice("dev-2", new[] { new SlotUsage(9, "Other", "JCM", Array.Empty<string>()) })
            .WithDevice("dev-1", new[] { new SlotUsage(1, "New", "AC30", Array.Empty<string>()) });
        Assert.Equal("New", Assert.Single(cache.SlotsFor("dev-1")).PresetName);
        Assert.Equal("Other", Assert.Single(cache.SlotsFor("dev-2")).PresetName);
    }

    [Fact]
    public void Prunes_oldest_devices_beyond_MaxDevices()
    {
        var cache = PresetUsageCache.Load(TempPath());
        for (int i = 0; i < PresetUsageCache.MaxDevices + 2; i++)
            cache = cache.WithDevice($"dev-{i}", SampleSlots);
        Assert.Empty(cache.SlotsFor("dev-0"));                      // oldest pruned
        Assert.NotEmpty(cache.SlotsFor($"dev-{PresetUsageCache.MaxDevices + 1}"));
    }

    [Fact]
    public void Prunes_oldest_insertion_when_all_savedUtc_tie()
    {
        // MaxDevices + 1 = 9 devices, all with an IDENTICAL savedUtc, dev-0..dev-8 in file
        // (insertion) order. The tie must be broken by insertion order, not by whatever order
        // a naive stable-sort-then-Take happens to preserve: dev-0 (earliest in file, i.e. the
        // oldest inserted) must be pruned, while dev-8 (latest in file) and the brand-new
        // dev-new (inserted after load) must both survive.
        var path = TempPath();
        try
        {
            var devices = string.Join(",", Enumerable.Range(0, PresetUsageCache.MaxDevices + 1)
                .Select(i => $$"""
                { "id": "dev-{{i}}", "savedUtc": "2026-08-03T00:00:00Z",
                  "slots": [ { "slot": 0, "preset": "P{{i}}", "amp": "Plexi", "irs": [] } ] }
                """));
            File.WriteAllText(path, $$"""{ "schema": 1, "devices": [ {{devices}} ] }""");

            var cache = PresetUsageCache.Load(path)
                .WithDevice("dev-new", new[] { new SlotUsage(0, "New", "AC30", Array.Empty<string>()) });

            Assert.Empty(cache.SlotsFor("dev-0"));
            Assert.NotEmpty(cache.SlotsFor($"dev-{PresetUsageCache.MaxDevices}"));
            Assert.NotEmpty(cache.SlotsFor("dev-new"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_drops_malformed_slot_entries()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, """
            { "schema": 1, "devices": [ { "id": "dev-1", "savedUtc": "2026-08-03T00:00:00Z",
              "slots": [
                { "slot": 0, "preset": "Good", "amp": "Plexi", "irs": [] },
                { "slot": 30, "preset": "OutOfRange", "amp": "X", "irs": [] },
                { "slot": -1, "preset": "Negative", "amp": "X", "irs": [] },
                { "slot": 2, "preset": "", "amp": "X", "irs": [] },
                { "slot": 3, "preset": "NullIrs", "amp": null, "irs": null } ] } ] }
            """);
            var rows = PresetUsageCache.Load(path).SlotsFor("dev-1");
            Assert.Equal(new[] { "Good", "NullIrs" }, rows.Select(r => r.PresetName));
            Assert.Empty(rows[1].Irs);                              // null irs → empty, not null
        }
        finally { File.Delete(path); }
    }
}
