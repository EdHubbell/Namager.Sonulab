using System.Linq;
using Sonulab.Core.Model;
using Sonulab.Core.Services;

namespace Namager.App.Services;

public interface IPresetUsageService
{
    /// <summary>Cached map of which presets use each amp/IR. Built on first call by reading
    /// every occupied preset document off the device; cached until <see cref="Invalidate"/>.</summary>
    Task<PresetUsageMap> GetAsync();

    /// <summary>Mark the cache stale — next <see cref="GetAsync"/> rebuilds. Call after any
    /// preset mutation (write/reorder/delete/duplicate/rename).</summary>
    void Invalidate();
}

/// <summary>Reads presets off the device once and caches the usage map. Shared by the preset,
/// amp and IR list VMs. Not concurrency-hardened: a double GetAsync may scan twice (idempotent,
/// harmless) — in practice calls are serialized by the busy-gated VM paths that invoke it.</summary>
public sealed class PresetUsageService : IPresetUsageService
{
    private readonly DeviceRepository _repo;
    private readonly IStatusService _status;
    private PresetUsageMap? _cache;

    public PresetUsageService(DeviceRepository repo, IStatusService? status = null)
    { _repo = repo; _status = status ?? NullStatusService.Instance; }

    public void Invalidate() => _cache = null;

    public async Task<PresetUsageMap> GetAsync()
    {
        if (_cache is { } cached) return cached;

        using var op = _status.BeginOperation("Checking preset usage…");
        var slots = await _repo.ListPresetsAsync();
        var docs = new List<(int, string, PresetDocument)>();
        foreach (var s in slots)
        {
            if (s.IsEmpty) continue;
            docs.Add((s.Index, s.Name, await _repo.ReadPresetAsync(s.Index)));
        }
        return _cache = PresetUsageMap.Build(docs);
    }
}

/// <summary>No-op fallback so a VM constructed without a usage service (existing tests) works —
/// nothing is ever "used".</summary>
public sealed class NullPresetUsageService : IPresetUsageService
{
    public static readonly NullPresetUsageService Instance = new();
    public Task<PresetUsageMap> GetAsync() => Task.FromResult(PresetUsageMap.Empty);
    public void Invalidate() { }
}

/// <summary>Formats preset references for display: "NN Name" (1-based, zero-padded slot to match
/// the preset list), joined by ", ", in the slot-ascending order the map already returns. Shared
/// by the amp/IR item tooltips and the delete/rename block message.</summary>
public static class PresetRefFormat
{
    public static string Join(IReadOnlyList<PresetRef> refs) =>
        string.Join(", ", refs.Select(r => $"{r.Index + 1:00} {r.Name}"));
}
