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

    /// <summary>Header for the synthetic block that fronts the pedal's per-preset output trim.</summary>
    public const string LevelBlockHeader = "Level";
    private const string levelKey = @"root\app\output\pst";

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
                                     CatalogVersion? catalog = null,
                                     Func<int, System.Threading.CancellationToken,
                                          Task<Sonulab.Distill.AmpMetadata?>>? readAmpMetadata = null,
                                     IPresetNavigator? navigator = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
        _status = status ?? NullStatusService.Instance;
        _repo = repo;
        _usage = usage ?? NullPresetUsageService.Instance;
        _catalog = catalog ?? new CatalogVersion();
        AmpDetail = new AmpDetailViewModel(
            readAmpMetadata ?? ((_, _) => Task.FromResult<Sonulab.Distill.AmpMetadata?>(null)),
            _usage, navigator);
    }

    /// <summary>#9: the amp referenced by this preset, shown in a read-only flyout off the amp field
    /// so you can decide whether it wants an IR without leaving the editor. Same control as the
    /// Amps tab renders inline.</summary>
    public AmpDetailViewModel AmpDetail { get; }

    /// <summary>Load the detail for the amp named by <paramref name="field"/>. Loaded on OPEN, not
    /// per selection: a metadata read on every preset click would put device reads on the hot path
    /// of browsing presets.</summary>
    [RelayCommand]
    private async Task ShowAmpDetailAsync(ParameterFieldViewModel? field)
    {
        if (field?.Text is not { Length: > 0 } name) { AmpDetail.Clear(); return; }
        try
        {
            // Resolve the name to a SLOT index against the RAW device list. field.Options cannot be
            // used: RefreshRefOptionsAsync filters empty slots out of the picker, so an option's
            // position is not its slot number. The raw list keeps the gaps.
            var names = await _client.ReadListAsync(@"root\amp");
            int index = -1;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal)) { index = i; break; }

            await AmpDetail.LoadAsync(index, name, isEmpty: false);
        }
        catch (Exception ex)
        {
            // [RelayCommand] async: an escape here is an unhandled UI-thread rethrow (process death).
            Log.Warn(ex, "amp detail flyout for '{0}' failed", name);
            AmpDetail.SetError($"Couldn't read the amp list: {ex.Message}");
        }
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

    // CanDownload depends on IsLoading, so every IsLoading transition must re-notify it —
    // not just the ones the load path happens to pass through.
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanDownload));

    /// <summary>Default file name offered by the download picker — the same "NN - Name.pst" form
    /// BackupService writes, so downloads drop straight into a backup folder.</summary>
    public string SuggestedFileName => LoadedIndex >= 0
        ? PresetFileNaming.FileNameFor(LoadedIndex, PresetName) : "preset.pst";

    // Per-session expansion memory, keyed by block path (root\app\<block>) so it survives
    // header relabeling; reapplied on every rebuild (preset switch). Intentionally NOT
    // persisted to disk (spec decision).
    private readonly Dictionary<string, bool> _expansion = new(StringComparer.Ordinal);

    private static readonly string[] EditableTypes = { "float", "enum", "plist" };

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

        // The pedal's per-preset output trim, promoted to its own block at the top of the editor.
        // Deliberately NOT a Blocks_InScope entry: `root\app\output` is the GLOBAL Master block
        // (its `vol` is the master volume, not a preset value), and the only other leaf under
        // `pst` is a per-preset BPM we don't surface. Addressed by exact path so nothing else
        // under `output` can leak in, which also means no hidden-params.json entry is needed.
        var levelRec = records.FirstOrDefault(r => r.Path == Sonulab.Distill.LevelModel.PresetLevelPath);
        if (levelRec is not null)
        {
            var levelSchema = NodeSchema.FromRecord(levelRec);
            var levelSection = new BlockSectionViewModel(LevelBlockHeader) { ShowLevelIcon = true };
            var levelValue = levelRec.Json.TryGetProperty("value", out var lv) ? lv.GetRawText() : "0";
            var levelField = new ParameterFieldViewModel(levelSchema, levelValue)
            {
                Label = _labels.Label(levelSchema.Path, levelSchema.Desc.Length > 0 ? levelSchema.Desc : null),
                ShowReset = true,
            };
            WireDirtyTracking(levelField);
            levelSection.Fields.Add(levelField);
            // Expanded by default — unlike every other block. This is the headline control and
            // was invisible before; a collapsed default would leave it just as hard to find.
            // The per-session memory still wins once the user has collapsed it.
            levelSection.IsExpanded = !_expansion.TryGetValue(levelKey, out var lexp) || lexp;
            WireExpansionMemory(levelSection, levelKey);
            Blocks.Add(levelSection);
        }

        foreach (var block in Blocks_InScope)
        {
            var prefix = @"root\app\" + block;
            var section = new BlockSectionViewModel(_labels.Label(prefix, DescOf(records, prefix)))
            {
                // `eq` is the only block with no on_off field, so its header icon slot is free.
                ShowEqIcon = string.Equals(block, "eq", StringComparison.OrdinalIgnoreCase),
            };
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
                // Reset on every float. fw 2.5.1 publishes `def` for all 86 float nodes, and only
                // the 4 EQ bands default to 0 — 58 of the rest do not (gate threshold -60 dB, comp
                // release 400 ms), so this is the only way back to factory without a manual.
                labeled.ShowReset = labeled.Kind == "float";
                WireDirtyTracking(labeled);

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
                WireExpansionMemory(section, prefix);
                Blocks.Add(section);
            }
        }
        IsDirty = false;
    }

    /// <summary>Subscribe a field so a genuine VALUE edit dirties the preset. Only a VALUE edit
    /// dirties the preset: Options/Kind change when the device's amp or IR list is refreshed under
    /// us (RefreshRefOptionsAsync -> SetRefOptions), which is not a user edit. Shared by the Level
    /// block and every <see cref="Blocks_InScope"/> field so the save-dirtying rule cannot drift
    /// between them.</summary>
    private void WireDirtyTracking(ParameterFieldViewModel field) =>
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ParameterFieldViewModel.Number)
                               or nameof(ParameterFieldViewModel.Text)) IsDirty = true;
        };

    /// <summary>Persist <paramref name="section"/>'s expansion into <see cref="_expansion"/> under
    /// <paramref name="key"/> (a stable block PATH, not the header — see the field's own comment)
    /// whenever the user toggles it. Shared by the Level block and every
    /// <see cref="Blocks_InScope"/> block.</summary>
    private void WireExpansionMemory(BlockSectionViewModel section, string key) =>
        section.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(BlockSectionViewModel.IsExpanded) && s is BlockSectionViewModel b)
                _expansion[key] = b.IsExpanded;
        };

    /// <summary>Activate <paramref name="target"/> on the device, then load its params.
    /// The content load is skipped when the same preset is already loaded, but the slot index is
    /// updated regardless: a reorder moves the selected preset to a new slot without changing its
    /// name, and a stale index would make the post-save usage update patch the wrong slot.</summary>
    // ---- #10: single-flight, latest-wins preset activation ----
    // Selection changes arrive fire-and-forget (MainWindowViewModel wires PresetList.Selected ->
    // LoadForCommand.Execute). Before this, three quick clicks started three overlapping chains:
    // their `write root\app\preset` calls interleaved on the serial link and the pedal ended up on
    // whichever landed last, which need not be the highlighted preset. One chain runs at a time now;
    // a request arriving mid-flight REPLACES any pending one, so intermediate presets are dropped
    // rather than queued and audibly replayed. Supersession happens BEFORE a load starts, so there
    // is no stale-completion path to guard.
    private PresetTarget? _pendingTarget;
    private bool _loadRunning;

    /// <summary>Test seam: the currently running load chain, or null when idle. Awaiting it runs to
    /// quiescence — including any target that was pending when it started.</summary>
    public Task? PendingLoad { get; private set; }

    [RelayCommand]
    private Task LoadForAsync(PresetTarget? target)
    {
        if (target is null || string.IsNullOrEmpty(target.Name)) return Task.CompletedTask;
        _pendingTarget = target;                       // newest wins; any earlier pending is dropped
        if (_loadRunning) return PendingLoad ?? Task.CompletedTask;
        _loadRunning = true;
        return PendingLoad = DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_pendingTarget is { } target)
            {
                _pendingTarget = null;
                await LoadOneAsync(target);
            }
        }
        finally { _loadRunning = false; }
    }

    /// <summary>One activation + param load. Unchanged in substance from the original LoadForAsync;
    /// the difference is that it is only ever entered by DrainAsync, one at a time.</summary>
    private async Task LoadOneAsync(PresetTarget target)
    {
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
            IsLoading = false;   // OnIsLoadingChanged re-notifies CanDownload
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
