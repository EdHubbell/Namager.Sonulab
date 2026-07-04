using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;

namespace Sonulab.App.ViewModels;

public sealed partial class ParameterEditorViewModel : ObservableObject
{
    public static IReadOnlyList<string> Blocks_InScope { get; } = new[] { "gate", "exp", "comp", "amp", "eq", "ir", "delay", "reverb" };

    private readonly SonuClient _client;
    private readonly LabelService _labels;
    private readonly ParameterExposure _exposure;

    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null, ParameterExposure? exposure = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
    }

    public ObservableCollection<BlockSectionViewModel> Blocks { get; } = new();
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private bool _isLoading;
    private string? _loadedName;

    // Per-session expansion memory, keyed by block path (root\app\<block>) so it survives
    // header relabeling; reapplied on every rebuild (preset switch). Intentionally NOT
    // persisted to disk (spec decision).
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);

    private static readonly string[] EditableTypes = { "float", "enum", "plist" };

    [RelayCommand]
    private async Task LoadAsync()
    {
        Blocks.Clear();
        var records = await _client.BrowseRecordsAsync(@"root\app");

        // Prefetch each distinct ref'd device list once per load (amp/IR pickers). A failed or
        // empty read degrades that field to today's rendering — the load itself never fails.
        var refOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var rec in records)
        {
            var schema = NodeSchema.FromRecord(rec);
            if (schema.Ref is not { Length: > 0 } r || refOptions.ContainsKey(r)) continue;
            if (!EditableTypes.Contains(schema.Type)) continue;
            try
            {
                var names = await _client.ReadListAsync(r);
                refOptions[r] = names.Where(n => !string.IsNullOrEmpty(n)).ToArray();
            }
            catch { refOptions[r] = Array.Empty<string>(); }
        }

        foreach (var block in Blocks_InScope)
        {
            var prefix = @"root\app\" + block;
            var section = new BlockSectionViewModel(_labels.Label(prefix, DescOf(records, prefix)));
            var subgroups = new Dictionary<string, SubGroupViewModel>();

            foreach (var rec in records)
            {
                if (rec.Path != prefix && !rec.Path.StartsWith(prefix + "\\", StringComparison.Ordinal)) continue;
                var schema = NodeSchema.FromRecord(rec);
                if (!EditableTypes.Contains(schema.Type)) continue;     // skip folders/containers/modules
                if (_exposure.IsHidden(rec.Path)) continue;

                var seg = rec.Path.Split('\\');                          // [root, app, block, (folder?), leaf]
                var value = rec.Json.TryGetProperty("value", out var v) ? v.GetRawText() : "\"\"";
                var labeled = new ParameterFieldViewModel(schema, value,
                    schema.Ref is { Length: > 0 } fr && refOptions.TryGetValue(fr, out var opts) && opts.Count > 0
                        ? opts : null);
                labeled.Label = _labels.Label(rec.Path, schema.Desc.Length > 0 ? schema.Desc : null);
                labeled.PropertyChanged += (_, _) => IsDirty = true;

                if (seg.Length == 4)                                     // root\app\block\leaf
                {
                    section.Fields.Add(labeled);
                }
                else                                                     // root\app\block\folder\...\leaf
                {
                    var folderPath = prefix + "\\" + seg[3];
                    if (!subgroups.TryGetValue(folderPath, out var sub))
                    {
                        sub = new SubGroupViewModel(_labels.Label(folderPath, DescOf(records, folderPath)));
                        subgroups[folderPath] = sub;
                        section.SubGroups.Add(sub);
                    }
                    sub.Fields.Add(labeled);
                }
            }

            section.EnableField = section.Fields.FirstOrDefault(f => f.Path.EndsWith("\\on_off", StringComparison.Ordinal));
            if (section.Fields.Count > 0 || section.SubGroups.Count > 0)
            {
                section.IsExpanded = _expansion.TryGetValue(prefix, out var exp) && exp;
                var key = prefix;
                section.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(BlockSectionViewModel.IsExpanded) && s is BlockSectionViewModel b)
                        _expansion[key] = b.IsExpanded;
                };
                Blocks.Add(section);
            }
        }
        IsDirty = false;
    }

    /// <summary>Activate <paramref name="presetName"/> on the device, then load its params. No-op if already loaded.</summary>
    [RelayCommand]
    private async Task LoadForAsync(string presetName)
    {
        if (string.IsNullOrEmpty(presetName) || presetName == _loadedName) return;
        IsLoading = true;
        try
        {
            await _client.WriteAsync(@"root\app\preset", JsonString.Quote(presetName));   // select/activate on device
            await LoadAsync();                                                          // browse + rebuild blocks
            PresetName = presetName;
            _loadedName = presetName;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in AllFields().Where(f => f.IsDirty))
            await _client.WriteAsync(f.Path, f.ToJsonValue());
        if (!string.IsNullOrEmpty(PresetName))
            await _client.SaveAsync(@"root\app\preset", PresetName);
        foreach (var f in AllFields()) f.MarkClean();
        IsDirty = false;
    }

    private IEnumerable<ParameterFieldViewModel> AllFields() =>
        Blocks.SelectMany(b => b.Fields.Concat(b.SubGroups.SelectMany(s => s.Fields)));

    private static string? DescOf(IReadOnlyList<NodeRecord> recs, string path)
    {
        foreach (var r in recs)
            if (r.Path == path && r.Json.TryGetProperty("desc", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String)
                return d.GetString();
        return null;
    }
}
