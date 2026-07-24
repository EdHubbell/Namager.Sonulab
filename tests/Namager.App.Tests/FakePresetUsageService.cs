using Namager.App.Services;
using Sonulab.Core.Services;

/// <summary>Controllable usage service for VM tests: set <see cref="Map"/>/<see cref="Complete"/>,
/// raise <see cref="RaiseMapUpdated"/>, observe calls.</summary>
public sealed class FakePresetUsageService : IPresetUsageService
{
    public PresetUsageMap Map { get; set; } = PresetUsageMap.Empty;
    public bool Complete { get; set; } = true;
    public int InvalidateCount { get; private set; }
    public int EnsureScanningCount { get; private set; }
    public int EnsureCompleteCount { get; private set; }
    public int MovedCount { get; private set; }
    public (int From, int To)? LastMoved { get; private set; }
    public int RenamedCount { get; private set; }
    public int DeletedCount { get; private set; }

    // When set, EnsureCompleteAsync awaits this — lets a test hold a guard check in flight.
    public System.Threading.Tasks.TaskCompletionSource? Gate { get; set; }
    // When set, EnsureCompleteAsync throws (simulates a dead link — guards must stay closed).
    public System.Exception? FailWith { get; set; }

    public PresetUsageMap Current => Map;
    public bool IsComplete => Complete;
    public event System.Action? MapUpdated;
    public void RaiseMapUpdated() => MapUpdated?.Invoke();

    public void EnsureScanning() { EnsureScanningCount++; }
    public async System.Threading.Tasks.Task<PresetUsageMap> EnsureCompleteAsync(
        System.Threading.CancellationToken ct = default)
    {
        EnsureCompleteCount++;
        if (Gate is not null) await Gate.Task;
        if (FailWith is not null) throw FailWith;
        Complete = true;
        return Map;
    }
    public void Invalidate() { InvalidateCount++; Complete = false; }
    public void NotifyPresetMoved(int from, int to) { MovedCount++; LastMoved = (from, to); Map = Map.WithMovedSlot(from, to); RaiseMapUpdated(); }
    public void NotifyPresetRenamed(int index, string newName) { RenamedCount++; Map = Map.WithRenamedPreset(index, newName); RaiseMapUpdated(); }
    public void NotifyPresetDeleted(int index) { DeletedCount++; Map = Map.WithoutSlot(index); RaiseMapUpdated(); }
    public void Stop() { }

    // Build a map from raw amp/IR node lines. Each preset carries its 0-based slot.
    public static PresetUsageMap MapFor(params (int Slot, string Preset, string[] Lines)[] presets)
    {
        var docs = new System.Collections.Generic.List<(int, string, Sonulab.Core.Model.PresetDocument)>();
        foreach (var (slot, preset, lines) in presets)
        {
            var text = string.Join("\r\n", lines);
            var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
            System.Text.Encoding.ASCII.GetBytes(text).CopyTo(blob, 0);
            docs.Add((slot, preset, Sonulab.Core.Model.PresetDocument.Parse(blob)));
        }
        return PresetUsageMap.Build(docs);
    }

    public static string AmpLine(string name) => $@"root\app\amp\amp:{{""value"":""{name}""}}";
    public static string IrLine(string name) => $@"root\app\ir\ir:{{""value"":""{name}""}}";
}
