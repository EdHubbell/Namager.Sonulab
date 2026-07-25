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
    public int ContentWrittenCount { get; private set; }
    public int ContentChangedCount { get; private set; }
    public (int Index, string Name)? LastContentChanged { get; private set; }

    /// <summary>When set, NotifyPresetContentChangedAsync applies this instead of reading the
    /// device — lets a VM test assert the map actually moved, not just that the call happened.</summary>
    public Sonulab.Core.Model.PresetDocument? NextContentDoc { get; set; }

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

    public void NotifyPresetContentWritten(int index, string name, Sonulab.Core.Model.PresetDocument doc)
    {
        ContentWrittenCount++;
        LastContentChanged = (index, name);
        Map = Map.WithUpdatedPreset(index, name, doc);
        RaiseMapUpdated();
    }

    public System.Threading.Tasks.Task NotifyPresetContentChangedAsync(
        int index, string name, System.Threading.CancellationToken ct = default)
    {
        ContentChangedCount++;
        LastContentChanged = (index, name);
        if (NextContentDoc is { } d) { Map = Map.WithUpdatedPreset(index, name, d); RaiseMapUpdated(); }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>Build a PresetDocument from raw node lines (same encoding as MapFor).</summary>
    public static Sonulab.Core.Model.PresetDocument DocFor(params string[] lines)
    {
        var blob = new byte[Sonulab.Core.Model.PresetDocument.BlobSize];
        System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n", lines)).CopyTo(blob, 0);
        return Sonulab.Core.Model.PresetDocument.Parse(blob);
    }

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
