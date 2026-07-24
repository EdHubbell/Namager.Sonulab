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

    // TODO(Task 7): remove — kept so MainWindowViewModel compiles until its wiring task.
    public PresetUsageService(DeviceRepository repo, IStatusService? status) : this(repo) { }

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

    public void Stop() => _cts.Cancel();

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        try
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
        catch (OperationCanceledException) { /* Stop() or caller cancel */ }
        catch (Exception ex)
        {
            // Best-effort: a link failure ends the scan quietly (highlights stay partial/stale).
            // EnsureCompleteAsync observes the incomplete state and throws — guards stay CLOSED.
            Log.Warn(ex, "preset-usage scan aborted");
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
