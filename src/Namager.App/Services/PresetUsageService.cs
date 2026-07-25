using System.Linq;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Namager.App.Services;

public interface IPresetUsageService
{
    /// <summary>The latest built map. Empty before any scan; PARTIAL while a scan is running;
    /// possibly STALE after Invalidate() (kept for best-effort highlights — guards must use
    /// EnsureCompleteAsync instead).</summary>
    PresetUsageMap Current { get; }

    /// <summary>True when Current covers every occupied preset and no Invalidate() has
    /// happened since. The delete/rename guards may trust Current only when this is true.</summary>
    bool IsComplete { get; }

    /// <summary>Raised after each preset resolves and once when the scan completes.
    /// MAY fire on a background thread — subscribers must marshal to the UI thread.</summary>
    event Action? MapUpdated;

    /// <summary>Idempotent: start (or continue) the background scan if the map is incomplete.
    /// Returns immediately; progress arrives via MapUpdated.</summary>
    void EnsureScanning();

    /// <summary>Guard path: finish the scan NOW (foreground reads) and return the complete map.
    /// Throws if the scan cannot complete (link died) — callers must treat that as "blocked".</summary>
    Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default);

    /// <summary>A preset mutation happened: the map is stale. Keeps Current for best-effort
    /// highlights but clears IsComplete; the next EnsureScanning()/EnsureCompleteAsync() rescans.</summary>
    void Invalidate();

    /// <summary>A verified reorder happened: remap Current in place (no rescan), keep IsComplete,
    /// raise MapUpdated. Callers must invoke ONLY on verified success; on failure use Invalidate().</summary>
    void NotifyPresetMoved(int from, int to);

    /// <summary>A verified rename happened: update the ref name at <paramref name="index"/> in place.</summary>
    void NotifyPresetRenamed(int index, string newName);

    /// <summary>A verified delete happened: drop refs at <paramref name="index"/> in place.</summary>
    void NotifyPresetDeleted(int index);

    /// <summary>A verified single-slot WRITE happened and the caller already holds the document
    /// (upload / restore): replace that slot's refs in Current with no device I/O.</summary>
    void NotifyPresetContentWritten(int index, string name, Sonulab.Core.Model.PresetDocument doc);

    /// <summary>A verified in-place content change happened and the caller does NOT hold the
    /// document (parameter-editor save): head-read that slot ONLY and replace its refs. Never
    /// throws — a failed read degrades to <see cref="Invalidate"/> so the map is rebuilt rather
    /// than left silently wrong.</summary>
    Task NotifyPresetContentChangedAsync(int index, string name, CancellationToken ct = default);

    /// <summary>Cancel any background work (disconnect / reconnect).</summary>
    void Stop();
}

/// <summary>Background preset-usage scanner. Reads each occupied preset's HEAD (windowed,
/// ≤32 chunks — see DeviceRepository.ReadPresetHeadAsync) over the SonuClient background lane,
/// publishing a partial map after every preset. The scan therefore never blocks a tab and never
/// interleaves with user-initiated bursts (the lane waits for foreground quiet). Shared by the
/// preset, amp and IR list VMs; one instance per connection.</summary>
public sealed class PresetUsageService : IPresetUsageService
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly DeviceRepository _repo;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _scanTask;
    private int _version;                  // bumped by Invalidate: a running scan restarts
    private volatile bool _urgent;         // EnsureCompleteAsync: use the foreground lane
    private volatile PresetUsageMap _current = PresetUsageMap.Empty;
    private volatile bool _isComplete;

    public PresetUsageService(DeviceRepository repo) => _repo = repo;

    public PresetUsageMap Current => _current;
    public bool IsComplete => _isComplete;
    public event Action? MapUpdated;

    public void EnsureScanning()
    {
        lock (_sync)
        {
            if (_isComplete || _cts.IsCancellationRequested) return;
            if (_scanTask is { IsCompleted: false }) return;
            _scanTask = Task.Run(() => ScanLoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    public async Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default)
    {
        _urgent = true;
        try
        {
            while (!_isComplete)
            {
                ct.ThrowIfCancellationRequested();
                _cts.Token.ThrowIfCancellationRequested();
                Task scan;
                lock (_sync)
                {
                    if (_scanTask is null or { IsCompleted: true } && !_isComplete)
                        _scanTask = Task.Run(() => ScanLoopAsync(_cts.Token), CancellationToken.None);
                    scan = _scanTask!;
                }
                await scan.WaitAsync(ct);   // ScanLoop swallows its own errors; see below
                if (!_isComplete && scan.IsCompleted)
                    throw new InvalidOperationException("Preset-usage scan could not complete.");
            }
            return _current;
        }
        finally { _urgent = false; }
    }

    public void Invalidate()
    {
        lock (_sync) { _version++; _isComplete = false; }
        // _current is kept: stale highlights beat no highlights. Guards use EnsureCompleteAsync.
    }

    public void NotifyPresetMoved(int from, int to) => Apply(m => m.WithMovedSlot(from, to));
    public void NotifyPresetRenamed(int index, string newName) => Apply(m => m.WithRenamedPreset(index, newName));
    public void NotifyPresetDeleted(int index) => Apply(m => m.WithoutSlot(index));

    public void NotifyPresetContentWritten(int index, string name, Sonulab.Core.Model.PresetDocument doc)
        => Apply(m => m.WithUpdatedPreset(index, name, doc));

    public async Task NotifyPresetContentChangedAsync(int index, string name, CancellationToken ct = default)
    {
        try
        {
            // Foreground lane: this runs right after a user-initiated save, so it must not sit
            // behind the background scan's quiet-period wait.
            var doc = await _repo.ReadPresetHeadAsync(index, background: false, ct);
            NotifyPresetContentWritten(index, name, doc);
        }
        catch (Exception ex)
        {
            // Includes cancellation: whatever the reason, a map we can't update targetedly must be
            // rebuilt, not trusted. Never rethrown — the caller's save already succeeded.
            Log.Warn(ex, "targeted usage update for slot {0} failed; falling back to a full rescan", index);
            Invalidate();
        }
    }

    // Transform Current in place and notify. IsComplete is intentionally UNTOUCHED: if the map was
    // complete it stays complete (targeted maintenance); if a rescan is mid-flight it stays
    // incomplete and that scan re-derives the truth. Safe to call from the UI thread post-mutation.
    private void Apply(Func<PresetUsageMap, PresetUsageMap> transform)
    {
        _current = transform(_current);
        MapUpdated?.Invoke();
    }

    public void Stop() => _cts.Cancel();

    /// <summary>Bounded-retry wrapper: on WiFi the pedal intermittently returns a torn/empty
    /// record, and a single transient glitch must not kill the whole background scan. Up to
    /// <see cref="MaxAttempts"/> full passes (list read included) are attempted, 500 ms apart;
    /// only after the last attempt fails does the scan give up (fail-closed: <see cref="_isComplete"/>
    /// stays false, EnsureCompleteAsync still throws). OperationCanceledException (Stop() or a
    /// caller cancel) exits immediately without retrying.</summary>
    private const int MaxAttempts = 3;

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await RunScanPassAsync(ct);
                return;                                     // completed (or a version-restart ran its course)
            }
            catch (OperationCanceledException) { return; }  // Stop() or caller cancel — no retry
            catch (Exception ex)
            {
                if (attempt >= MaxAttempts)
                {
                    // Best-effort: a link failure ends the scan quietly (highlights stay partial/stale).
                    // EnsureCompleteAsync observes the incomplete state and throws — guards stay CLOSED.
                    Log.Warn(ex, "preset-usage scan aborted after {Attempt} attempts", attempt);
                    return;
                }
                Log.Warn(ex, "preset-usage scan attempt {Attempt} failed; retrying", attempt);
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>One scan pass: list the occupied slots, then head-read each one, restarting from
    /// the top whenever Invalidate() bumps the version mid-pass. Exceptions propagate to the
    /// caller's retry loop; this method makes no attempt to swallow them.</summary>
    private async Task RunScanPassAsync(CancellationToken ct)
    {
        while (true)
        {
            int version; lock (_sync) version = _version;
            var slots = _urgent
                ? await _repo.ListPresetsAsync(ct)
                : await _repo.ListPresetsBackgroundAsync(ct);
            var resolved = new List<(int, string, Sonulab.Core.Model.PresetDocument)>();
            bool restart = false;
            foreach (var s in slots)
            {
                if (s.IsEmpty) continue;
                ct.ThrowIfCancellationRequested();
                lock (_sync) { if (_version != version) { restart = true; } }
                if (restart) break;
                var doc = await _repo.ReadPresetHeadAsync(s.Index, background: !_urgent, ct);
                resolved.Add((s.Index, s.Name, doc));
                _current = PresetUsageMap.Build(resolved);
                MapUpdated?.Invoke();
            }
            if (restart) continue;                       // stale version: rescan from the top
            lock (_sync) { if (_version == version) _isComplete = true; else continue; }
            MapUpdated?.Invoke();
            return;
        }
    }
}

/// <summary>No-op fallback so a VM constructed without a usage service (existing tests) works —
/// nothing is ever "used", the map reports complete, guards never block.</summary>
public sealed class NullPresetUsageService : IPresetUsageService
{
    public static readonly NullPresetUsageService Instance = new();
    public PresetUsageMap Current => PresetUsageMap.Empty;
    public bool IsComplete => true;
    public event Action? MapUpdated { add { } remove { } }
    public void EnsureScanning() { }
    public Task<PresetUsageMap> EnsureCompleteAsync(CancellationToken ct = default)
        => Task.FromResult(PresetUsageMap.Empty);
    public void Invalidate() { }
    public void NotifyPresetMoved(int from, int to) { }
    public void NotifyPresetRenamed(int index, string newName) { }
    public void NotifyPresetDeleted(int index) { }
    public void NotifyPresetContentWritten(int index, string name, Sonulab.Core.Model.PresetDocument doc) { }
    public Task NotifyPresetContentChangedAsync(int index, string name, CancellationToken ct = default)
        => Task.CompletedTask;
    public void Stop() { }
}

/// <summary>Formats preset references for display: "NN Name" (1-based, zero-padded slot to match
/// the preset list), joined by ", ", in the slot-ascending order the map already returns. Shared
/// by the amp/IR item tooltips and the delete/rename block message.</summary>
public static class PresetRefFormat
{
    public static string Join(IReadOnlyList<PresetRef> refs) =>
        string.Join(", ", refs.Select(r => $"{r.Index + 1:00} {r.Name}"));
}
