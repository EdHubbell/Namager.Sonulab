using Namager.App.Services;
using Sonulab.Core.Services;

/// <summary>Controllable usage service for VM tests: set <see cref="Map"/>, observe calls.</summary>
public sealed class FakePresetUsageService : IPresetUsageService
{
    public PresetUsageMap Map { get; set; } = PresetUsageMap.Empty;
    public int InvalidateCount { get; private set; }
    public int GetCount { get; private set; }

    // When set, GetAsync awaits this before returning — lets a test hold a usage scan in flight.
    public System.Threading.Tasks.TaskCompletionSource? Gate { get; set; }

    public async System.Threading.Tasks.Task<PresetUsageMap> GetAsync()
    {
        GetCount++;
        if (Gate is not null) await Gate.Task;
        return Map;
    }
    public void Invalidate() { InvalidateCount++; }

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

    public static string AmpLine(string name) => $@"root\app\amp\amp:{{""value"":""{name}"",""ref"":""root\\amp""}}";
    public static string IrLine(string name) => $@"root\app\ir\ir:{{""value"":""{name}"",""ref"":""root\\ir""}}";
}
