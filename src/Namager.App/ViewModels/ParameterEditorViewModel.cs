using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Namager.App.Services;
using Sonulab.Core;
using Sonulab.Core.Model;
using Sonulab.Core.Protocol;
// Sonulab.Core.Services.DeviceRepository is referenced fully-qualified below to avoid a broad using.

namespace Namager.App.ViewModels;

public sealed partial class ParameterEditorViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    public static IReadOnlyList<string> Blocks_InScope { get; } = new[] { "gate", "exp", "comp", "amp", "eq", "ir", "delay", "reverb" };

    private readonly SonuClient _client;
    private readonly LabelService _labels;
    private readonly ParameterExposure _exposure;
    private readonly IStatusService _status;
    private readonly Sonulab.Core.Services.DeviceRepository? _repo;
    private readonly IPresetUsageService _usage;
    private readonly CatalogVersion _catalog;
    private int _optionsVersion = -1;

    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null,
                                     ParameterExposure? exposure = null,
                                     IStatusService? status = null,
                                     Sonulab.Core.Services.DeviceRepository? repo = null,
                                     IPresetUsageService? usage = null,
                                     CatalogVersion? catalog = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
        _status = status ?? NullStatusService.Instance;
        _repo = repo;
        _usage = usage ?? NullPresetUsageService.Instance;
        _catalog = catalog ?? new CatalogVersion();
    }

    public ObservableCollection<BlockSectionViewModel> Blocks { get; } = new();
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _presetName = "";
    [ObservableProperty] private bool _isLoading;
    /// <summary>Last device-operation failure, shown to the user. Null when the last op succeeded.</summary>
    [ObservableProperty] private string? _errorMessage;
    private string? _loadedName;

    /// <summary>0-based device slot of the loaded preset, or -1 when nothing is loaded.</summary>
    public int LoadedIndex { get; private set; } = -1;

    /// <summary>A preset is loaded and a repository is available, so its bytes can be read.</summary>
    public bool CanDownload => _repo is not null && LoadedIndex >= 0 && !IsLoading;

    /// <summary>Default file name offered by the download picker — the same "NN - Name.pst" form
    /// BackupService writes, so downloads drop straight into a backup folder.</summary>
    public string SuggestedFileName => LoadedIndex >= 0
        ? PresetFileNaming.FileNameFor(LoadedIndex, PresetName) : "preset.pst";

    // Per-session expansion memory, keyed by block path (root\app\<block>) so it survives
    // header relabeling; reapplied on every rebuild (preset switch). Intentionally NOT
    // persisted to disk (spec decision).
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);

    private static readonly string[] EditableTypes = { "float", "enum", "plist" };

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Crash-guard (field crash class, v0.9.3 test build): a device failure — e.g. the WiFi link
        // dying mid-session — must surface as ErrorMessage, never escape the [RelayCommand].
        ErrorMessage = null;
        try { await LoadCoreAsync(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "parameter load failed");
            ErrorMessage = $"Load failed: {ex.Message}";
        }
    }

    private async Task LoadCoreAsync()
    {
        Blocks.Clear();
        var records = await _client.BrowseRecordsAsync(@"root\app");

        // Capture BEFORE the reads: a bump that lands mid-load must not be swallowed.
        int catalogAtLoad = _catalog.Version;

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
        _optionsVersion = catalogAtLoad;

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

    /// <summary>Activate <paramref name="target"/> on the device, then load its params.
    /// The content load is skipped when the same preset is already loaded, but the slot index is
    /// updated regardless: a reorder moves the selected preset to a new slot without changing its
    /// name, and a stale index would make the post-save usage update patch the wrong slot.</summary>
    [RelayCommand]
    private async Task LoadForAsync(PresetTarget? target)
    {
        if (target is null || string.IsNullOrEmpty(target.Name)) return;
        int previousIndex = LoadedIndex;
        LoadedIndex = target.Index;
        if (target.Name == _loadedName) return;
        IsLoading = true; ErrorMessage = null;
        try
        {
            await _client.WriteAsync(@"root\app\preset", JsonString.Quote(target.Name));   // select/activate on device
            await LoadCoreAsync();                                                        // browse + rebuild blocks
            PresetName = target.Name;
            _loadedName = target.Name;   // only marked loaded on success — reselecting retries
        }
        catch (Exception ex)
        {
            // Fired on preset selection (PropertyChanged -> Execute) — an escape here is an
            // unhandled UI-thread rethrow, i.e. process death. Surface and stay alive.
            // LoadedIndex is rolled back too: PresetName/_loadedName still describe the
            // PREVIOUSLY loaded preset on failure, so the index must match or a later
            // download/usage-notify targets the wrong slot under the wrong name.
            Log.Warn(ex, "parameter load-for '{0}' failed", target.Name);
            ErrorMessage = $"Load failed: {ex.Message}";
            LoadedIndex = previousIndex;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(SuggestedFileName));
        }
    }

    /// <summary>Re-read the amp/IR picker lists if the device catalog moved since they were
    /// loaded. Called when the user lands on the Presets tab. Cheap (one `read` per distinct ref
    /// target), replaces OPTIONS only — never values — so unsaved edits survive, and never throws:
    /// a failed read leaves the old options in place and retries on the next visit.</summary>
    public async Task RefreshRefOptionsAsync()
    {
        int version = _catalog.Version;
        if (version == _optionsVersion || Blocks.Count == 0) return;

        var sources = AllFields().Select(f => f.RefSource)
                                 .Where(s => s is { Length: > 0 })
                                 .Distinct(StringComparer.Ordinal)
                                 .ToArray();
        if (sources.Length == 0) { _optionsVersion = version; return; }

        bool allOk = true;
        foreach (var src in sources)
        {
            try
            {
                var names = (await _client.ReadListAsync(src!))
                    .Where(n => !string.IsNullOrEmpty(n)).ToArray();
                foreach (var f in AllFields().Where(f => f.RefSource == src))
                    f.SetRefOptions(names);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "ref-option refresh for '{0}' failed", src);
                allOk = false;
            }
        }
        if (allOk) _optionsVersion = version;
    }

    /// <summary>Read the loaded preset's full 8192-byte blob from the pedal. The VIEW writes it to
    /// the path the user picked — keeping the file dialog out of the view model. Returns null (and
    /// sets <see cref="ErrorMessage"/>) on failure; never throws.</summary>
    public async Task<byte[]?> ReadLoadedPresetBytesAsync()
    {
        if (_repo is null || LoadedIndex < 0) return null;
        ErrorMessage = null;
        using var op = _status.BeginOperation("Reading preset…");
        try
        {
            return (await _repo.ReadPresetAsync(LoadedIndex)).ToBytes();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "preset download read failed");
            ErrorMessage = $"Download failed: {ex.Message}";
            _status.Failure($"Download failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Report a completed download on the status bar.</summary>
    public void ReportDownloaded(string path)
        => _status.Success($"Saved {System.IO.Path.GetFileName(path)}");

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        using var op = _status.BeginOperation("Saving preset…");
        try
        {
            foreach (var f in AllFields().Where(f => f.IsDirty))
                await _client.WriteAsync(f.Path, f.ToJsonValue());
            if (!string.IsNullOrEmpty(PresetName))
                await _client.SaveAsync(@"root\app\preset", PresetName);
            foreach (var f in AllFields()) f.MarkClean();
            IsDirty = false;
            _status.Success("Saved");

            // Targeted usage-map maintenance: an amp/IR change made here must be visible on the
            // Amps/IRs tabs immediately, or the delete guard keeps blocking an amp nothing uses.
            // Wrapped separately so a map failure can never be reported as a failed save.
            if (LoadedIndex >= 0)
            {
                try { await _usage.NotifyPresetContentChangedAsync(LoadedIndex, PresetName); }
                catch (Exception ex) { Log.Warn(ex, "usage map update after save failed"); }
            }
        }
        catch (Exception ex)
        {
            // Fields stay dirty on failure so the user can retry the save after reconnecting.
            Log.Warn(ex, "parameter save failed");
            ErrorMessage = $"Save failed: {ex.Message}";
            _status.Failure($"Save failed: {ex.Message}");
        }
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
