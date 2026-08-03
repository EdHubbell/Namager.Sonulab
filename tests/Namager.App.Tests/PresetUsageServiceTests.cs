// tests/Namager.App.Tests/PresetUsageServiceTests.cs
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Services;
using Xunit;

public class PresetUsageServiceTests
{
    private const string AmpNode = @"root\app\amp\amp:{{""value"":""{0}""}}";
    private static string Amp(string name) => string.Format(AmpNode, name);

    private static (PresetUsageService svc, FakePresetDevice dev, CountingLink link) Make()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        // backgroundQuietMs 0: tests exercise scan logic, not the lane (lane has its own tests)
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        return (new PresetUsageService(repo), dev, link);
    }

    /// <summary>Seeds a device with the given (index, presetName, ampName) presets, opens a
    /// PresetUsageService over it, and runs the scan to completion. Used by the targeted-notify
    /// tests, which need a service whose IsComplete is already true.</summary>
    private static async Task<(PresetUsageService svc, DeviceRepository repo)> CompletedService(
        params (int Index, string PresetName, string AmpName)[] presets)
    {
        var dev = new FakePresetDevice();
        foreach (var (index, presetName, ampName) in presets)
            dev.SeedSlot(index, presetName, new[] { Amp(ampName) });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);
        await svc.EnsureCompleteAsync();
        return (svc, repo);
    }

    [Fact]
    public async Task EnsureComplete_builds_the_full_map()
    {
        var (svc, _, _) = Make();
        var map = await svc.EnsureCompleteAsync();
        Assert.True(svc.IsComplete);
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
    }

    [Fact]
    public async Task Scan_is_progressive_and_raises_MapUpdated_per_preset()
    {
        var (svc, _, _) = Make();
        int updates = 0;
        svc.MapUpdated += () => Interlocked.Increment(ref updates);
        svc.EnsureScanning();
        await svc.EnsureCompleteAsync();
        Assert.True(updates >= 2, $"expected per-preset updates, got {updates}");
        Assert.Single(svc.Current.PresetsUsingAmp("Plexi").Where(r => r.Index == 0));
    }

    [Fact]
    public async Task Complete_map_is_cached_until_invalidated()
    {
        var (svc, _, link) = Make();
        await svc.EnsureCompleteAsync();
        int afterFirst = link.Dreads;
        Assert.True(afterFirst > 0);

        await svc.EnsureCompleteAsync();
        Assert.Equal(afterFirst, link.Dreads);              // cache hit

        svc.Invalidate();
        Assert.False(svc.IsComplete);
        Assert.NotSame(PresetUsageMap.Empty, svc.Current);  // stale map kept for highlights
        await svc.EnsureCompleteAsync();
        Assert.True(link.Dreads > afterFirst);              // rescan happened
    }

    [Fact]
    public async Task Invalidate_during_a_scan_restarts_it()
    {
        var (svc, dev, _) = Make();
        svc.EnsureScanning();
        svc.Invalidate();
        dev.SeedSlot(2, "New", new[] { Amp("JCM800") });
        var map = await svc.EnsureCompleteAsync();
        Assert.Single(map.PresetsUsingAmp("JCM800"));       // post-invalidate content included
    }

    [Fact]
    public async Task EnsureComplete_throws_when_the_link_is_dead_and_guards_stay_closed()
    {
        var dev = new FakePresetDevice();                   // never opened → SendAsync throws
        var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
        Assert.False(svc.IsComplete);
    }

    [Fact]
    public async Task Stop_cancels_a_running_scan()
    {
        var (svc, _, _) = Make();
        svc.EnsureScanning();
        svc.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
    }

    [Fact]
    public async Task Torn_preset_read_aborts_the_scan_and_guards_stay_closed()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new TornIndexLink(dev, tearIndex: 1);
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);

        // The tear is permanent (every attempt hits it), so all 3 bounded retries fail and the
        // scan still gives up — guards stay closed. (Not asserting attempt count: that's the
        // retry loop's business, not this test's.)
        await Assert.ThrowsAnyAsync<Exception>(() => svc.EnsureCompleteAsync());
        Assert.False(svc.IsComplete);
    }

    [Fact]
    public async Task Transient_read_failure_is_retried_and_the_scan_completes()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new FlakyOnceLink(dev);
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        var svc = new PresetUsageService(repo);

        var map = await svc.EnsureCompleteAsync();

        Assert.True(svc.IsComplete);
        Assert.Equal(new[] { new PresetRef(0, "Lead"), new PresetRef(1, "Rhythm") },
                     map.PresetsUsingAmp("Plexi"));
        Assert.True(link.ListAttempts >= 2, $"expected a retry, got {link.ListAttempts} attempt(s)");
    }

    [Fact]
    public async Task NotifyPresetMoved_remaps_current_and_keeps_complete()
    {
        var (svc, _) = await CompletedService(
            (0, "A", "mA"), (1, "B", "mB"), (2, "C", "mC"));
        Assert.True(svc.IsComplete);
        int updates = 0; svc.MapUpdated += () => updates++;

        svc.NotifyPresetMoved(0, 2);   // A moves 0 -> 2

        Assert.True(svc.IsComplete);   // targeted maintenance does NOT drop completeness
        Assert.Equal(1, updates);
        Assert.Equal(2, svc.Current.PresetsUsingAmp("mA")[0].Index);
    }

    [Fact]
    public async Task NotifyPresetDeleted_drops_refs_and_keeps_complete()
    {
        var (svc, _) = await CompletedService((0, "A", "mA"), (1, "B", "mB"));
        svc.NotifyPresetDeleted(0);
        Assert.True(svc.IsComplete);
        Assert.Empty(svc.Current.PresetsUsingAmp("mA"));
    }

    [Fact]
    public async Task NotifyPresetRenamed_updates_ref_name()
    {
        var (svc, _) = await CompletedService((0, "A", "mA"));
        svc.NotifyPresetRenamed(0, "A2");
        Assert.Equal("A2", svc.Current.PresetsUsingAmp("mA")[0].Name);
    }

    /// <summary>Link wrapper that returns an empty response ("torn read") for any dread targeting
    /// one specific preset index, and delegates everything else.</summary>
    private sealed class TornIndexLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        private readonly int _tearIndex;
        public TornIndexLink(Sonulab.Core.Transport.ISonuLink inner, int tearIndex)
        {
            _inner = inner;
            _tearIndex = tearIndex;
        }
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)
                && command.Contains($"\"index\":{_tearIndex}", StringComparison.Ordinal))
                return Task.FromResult("");
            return _inner.SendAsync(command, ct);
        }
    }

    /// <summary>Link wrapper that fails the preset-list read ("" — a torn/empty record, matching
    /// the WiFi glitch this fix targets) on the FIRST attempt only; every later attempt (and every
    /// other command) passes through untouched — proves a transient failure is retried rather
    /// than killing the scan.</summary>
    private sealed class FlakyOnceLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        private bool _failedOnce;
        public int ListAttempts;
        public FlakyOnceLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith(@"read root\presets", StringComparison.Ordinal))
            {
                ListAttempts++;
                if (!_failedOnce) { _failedOnce = true; return Task.FromResult(""); }
            }
            return _inner.SendAsync(command, ct);
        }
    }

    [Fact] public async Task NotifyPresetContentChanged_repoints_a_single_slot_without_a_full_rescan()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var repo = new DeviceRepository(new SonuClient(dev));
        var svc = new PresetUsageService(repo);

        await svc.EnsureCompleteAsync();
        Assert.Equal(2, svc.Current.PresetsUsingAmp("mA").Count);

        dev.SeedSlot(1, "B", new[] { @"root\app\amp\amp:{""value"":""mB""}" });
        await svc.NotifyPresetContentChangedAsync(1, "B");

        Assert.Equal(new[] { 0 }, svc.Current.PresetsUsingAmp("mA").Select(r => r.Index));
        Assert.Equal(new[] { 1 }, svc.Current.PresetsUsingAmp("mB").Select(r => r.Index));
        Assert.True(svc.IsComplete);          // targeted maintenance must NOT drop completeness
    }

    [Fact] public async Task NotifyPresetContentChanged_raises_MapUpdated()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(dev)));
        await svc.EnsureCompleteAsync();

        int raised = 0;
        svc.MapUpdated += () => raised++;
        await svc.NotifyPresetContentChangedAsync(0, "A");
        Assert.True(raised > 0);
    }

    [Fact] public async Task NotifyPresetContentChanged_falls_back_to_Invalidate_when_the_read_fails()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(dev)));
        await svc.EnsureCompleteAsync();
        Assert.True(svc.IsComplete);

        dev.Close();                                              // every later read throws
        await svc.NotifyPresetContentChangedAsync(0, "A");        // must NOT throw

        Assert.False(svc.IsComplete);                             // degraded to "rescan needed"
    }

    [Fact] public async Task NotifyPresetContentWritten_applies_the_document_with_no_device_read()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        await dev.OpenAsync();
        var svc = new PresetUsageService(new DeviceRepository(new SonuClient(dev)));
        await svc.EnsureCompleteAsync();

        dev.Close();                                              // proves no read happens
        svc.NotifyPresetContentWritten(0, "A", FakePresetUsageService.DocFor(
            @"root\app\amp\amp:{""value"":""mZ""}"));

        Assert.Equal(new[] { 0 }, svc.Current.PresetsUsingAmp("mZ").Select(r => r.Index));
        Assert.Empty(svc.Current.PresetsUsingAmp("mA"));
    }

    private sealed class CountingLink : Sonulab.Core.Transport.ISonuLink
    {
        private readonly Sonulab.Core.Transport.ISonuLink _inner;
        public int Dreads;
        public CountingLink(Sonulab.Core.Transport.ISonuLink inner) => _inner = inner;
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(CancellationToken ct = default) => _inner.OpenAsync(ct);
        public void Close() => _inner.Close();
        public Task<string> SendAsync(string command, CancellationToken ct = default)
        {
            if (command.StartsWith("dread ", StringComparison.Ordinal)) Dreads++;
            return _inner.SendAsync(command, ct);
        }
    }

    // ---- warm start from the disk cache ----

    private const string IrNode = @"root\app\ir\ir:{{""value"":""{0}""}}";
    private static string Ir(string name) => string.Format(IrNode, name);

    /// <summary>Make() variant with cache seams. Seeds the same two presets as Make().</summary>
    private static (PresetUsageService svc, FakePresetDevice dev, CountingLink link) MakeCached(
        string? deviceId, string cachePath)
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
        dev.SeedSlot(1, "Rhythm", new[] { Amp("Plexi") });
        dev.OpenAsync().GetAwaiter().GetResult();
        var link = new CountingLink(dev);
        var repo = new DeviceRepository(new SonuClient(link, backgroundQuietMs: 0));
        return (new PresetUsageService(repo, deviceId, cachePath), dev, link);
    }

    private static string TempCachePath() =>
        Path.Combine(Path.GetTempPath(), $"nmgr-usage-svc-{Guid.NewGuid():N}.json");

    private static void SeedCache(string path, string deviceId, params SlotUsage[] slots) =>
        PresetUsageCache.Load(path).WithDevice(deviceId, slots).Save(path);

    [Fact]
    public async Task Warm_start_publishes_cached_map_at_zero_dreads()
    {
        var path = TempCachePath();
        try
        {
            SeedCache(path, "dev-1",
                new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()),
                new SlotUsage(1, "Rhythm", "Plexi", Array.Empty<string>()));
            var (svc, _, link) = MakeCached("dev-1", path);

            var snapshots = new List<(int Dreads, PresetUsageMap Map, bool Complete)>();
            svc.MapUpdated += () => { lock (snapshots) snapshots.Add((link.Dreads, svc.Current, svc.IsComplete)); };
            await svc.EnsureCompleteAsync();

            (int Dreads, PresetUsageMap Map, bool Complete) first;
            lock (snapshots) first = snapshots[0];
            Assert.Equal(0, first.Dreads);                       // provisional publish cost no dreads
            Assert.Equal(2, first.Map.PresetsUsingAmp("Plexi").Count);
            Assert.False(first.Complete);                        // cached data never completes
            Assert.True(svc.IsComplete);                         // ...but the real scan does
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cached_slot_with_mismatched_name_is_dropped_from_the_provisional_map()
    {
        var path = TempCachePath();
        try
        {
            SeedCache(path, "dev-1",
                new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()),
                new SlotUsage(1, "RENAMED-OUTSIDE", "Ghost", Array.Empty<string>()));
            var (svc, _, link) = MakeCached("dev-1", path);

            var firstMaps = new List<(int Dreads, PresetUsageMap Map)>();
            svc.MapUpdated += () => { lock (firstMaps) firstMaps.Add((link.Dreads, svc.Current)); };
            await svc.EnsureCompleteAsync();

            (int Dreads, PresetUsageMap Map) first;
            lock (firstMaps) first = firstMaps[0];
            Assert.Equal(0, first.Dreads);
            Assert.Empty(first.Map.PresetsUsingAmp("Ghost"));    // stale row dropped by name mismatch
            Assert.Single(first.Map.PresetsUsingAmp("Plexi"));   // matching row kept
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cache_for_a_different_device_is_ignored()
    {
        var path = TempCachePath();
        try
        {
            SeedCache(path, "other-pedal", new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()));
            var (svc, _, link) = MakeCached("dev-1", path);

            var snapshots = new List<int>();
            svc.MapUpdated += () => { lock (snapshots) snapshots.Add(link.Dreads); };
            await svc.EnsureCompleteAsync();

            int firstDreads; lock (snapshots) firstDreads = snapshots[0];
            Assert.True(firstDreads > 0, "no zero-dread provisional publish for a foreign device");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Scan_corrects_a_lying_cache_and_persists_the_truth()
    {
        var path = TempCachePath();
        try
        {
            SeedCache(path, "dev-1", new SlotUsage(0, "Lead", "WrongAmp", Array.Empty<string>()));
            var (svc, _, _) = MakeCached("dev-1", path);

            var map = await svc.EnsureCompleteAsync();
            Assert.Empty(map.PresetsUsingAmp("WrongAmp"));
            Assert.Equal(2, map.PresetsUsingAmp("Plexi").Count);

            var persisted = PresetUsageCache.Load(path).SlotsFor("dev-1");
            Assert.DoesNotContain(persisted, s => s.Amp == "WrongAmp");
            Assert.Equal(2, persisted.Count(s => s.Amp == "Plexi"));   // completion persisted truth
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Provisional_entries_survive_progressive_publishes()
    {
        var path = TempCachePath();
        try
        {
            // Cache knows slot 1; the scan reads slot 0 first. The slot-0 publish must not
            // flash slot 1's provisional highlight off.
            SeedCache(path, "dev-1", new SlotUsage(1, "Rhythm", "Plexi", Array.Empty<string>()));
            var (svc, _, _) = MakeCached("dev-1", path);

            var everyMapHadSlot1 = true;
            svc.MapUpdated += () =>
            {
                if (!svc.Current.PresetsUsingAmp("Plexi").Any(r => r.Index == 1))
                    everyMapHadSlot1 = false;
            };
            await svc.EnsureCompleteAsync();
            Assert.True(everyMapHadSlot1, "a progressive publish dropped the provisional slot-1 entry");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task No_deviceId_means_no_cache_reads_or_writes()
    {
        var path = TempCachePath();
        try
        {
            var (svc, _, _) = MakeCached(deviceId: null, path);
            await svc.EnsureCompleteAsync();
            Assert.False(File.Exists(path), "cacheless service must not write the cache file");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Targeted_notify_on_a_complete_map_updates_the_persisted_cache()
    {
        var path = TempCachePath();
        try
        {
            var dev = new FakePresetDevice();
            dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
            await dev.OpenAsync();
            var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
            var svc = new PresetUsageService(repo, "dev-1", path);
            await svc.EnsureCompleteAsync();

            svc.NotifyPresetRenamed(0, "Lead V2");
            var persisted = PresetUsageCache.Load(path).SlotsFor("dev-1");
            Assert.Equal("Lead V2", Assert.Single(persisted).PresetName);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Regression for a bug found in review: if the device empties out (all presets
    /// deleted outside the app) between a cache-seeded warm-start publish and the final stable
    /// pass, the final pass's occupied-slot loop runs zero times. Before the fix, nothing
    /// reassigned Current in that pass, so the stale/provisional map from the warm start rode
    /// through into a map marked IsComplete AND got persisted as "verified truth". The scan must
    /// instead certify (and persist) the actually-empty map.</summary>
    [Fact]
    public async Task Emptying_the_device_mid_scan_completes_to_an_empty_map_and_persists_it()
    {
        var path = TempCachePath();
        try
        {
            SeedCache(path, "dev-1", new SlotUsage(0, "Lead", "Plexi", Array.Empty<string>()));

            var dev = new FakePresetDevice();
            dev.SeedSlot(0, "Lead", new[] { Amp("Plexi") });
            await dev.OpenAsync();
            var repo = new DeviceRepository(new SonuClient(dev, backgroundQuietMs: 0));
            var svc = new PresetUsageService(repo, "dev-1", path);

            bool triggered = false;
            svc.MapUpdated += () =>
            {
                if (triggered) return;
                triggered = true;
                dev.SeedSlot(0, "", Array.Empty<string>());   // preset deleted outside the app
                svc.Invalidate();
            };

            var map = await svc.EnsureCompleteAsync();

            Assert.Empty(map.PresetsUsingAmp("Plexi"));          // no phantom ref survives
            Assert.True(svc.IsComplete);
            var persisted = PresetUsageCache.Load(path).SlotsFor("dev-1");
            Assert.Empty(persisted);                             // persisted truth is empty, not stale
        }
        finally { File.Delete(path); }
    }
}
