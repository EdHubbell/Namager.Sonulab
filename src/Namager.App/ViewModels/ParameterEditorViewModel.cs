using System.Collections.ObjectModel;
using System.Globalization;
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
    private readonly Func<int, CancellationToken, Task<byte[]>>? _readAmpBlob;
    private readonly Func<int, CancellationToken, Task<byte[]?>>? _readIrBlob;
    private readonly Func<int, CancellationToken, Task<Sonulab.Core.Model.PresetDocument>>? _readPresetDoc;

    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null,
                                     ParameterExposure? exposure = null,
                                     IStatusService? status = null,
                                     Sonulab.Core.Services.DeviceRepository? repo = null,
                                     IPresetUsageService? usage = null,
                                     CatalogVersion? catalog = null,
                                     Func<int, System.Threading.CancellationToken,
                                          Task<Sonulab.Distill.AmpMetadata?>>? readAmpMetadata = null,
                                     Func<int, CancellationToken, Task<byte[]>>? readAmpBlob = null,
                                     Func<int, CancellationToken, Task<byte[]?>>? readIrBlob = null,
                                     Func<int, CancellationToken, Task<Sonulab.Core.Model.PresetDocument>>? readPresetDoc = null,
                                     IPresetNavigator? navigator = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
        _status = status ?? NullStatusService.Instance;
        _repo = repo;
        _usage = usage ?? NullPresetUsageService.Instance;
        _catalog = catalog ?? new CatalogVersion();
        _readAmpBlob = readAmpBlob;
        _readIrBlob = readIrBlob;
        _readPresetDoc = readPresetDoc;
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

    /// <summary>The Preset Level field, or null when the firmware has no such node.</summary>
    public ParameterFieldViewModel? LevelField { get; private set; }

    /// <summary>Volume matching needs to read another preset and its amp model, so it is only
    /// offered when the app supplied those readers (the real app always does; unit tests that
    /// only exercise the editor do not).</summary>
    public bool CanMatchVolume =>
        _readAmpBlob is not null && _readPresetDoc is not null && LevelField is not null && !IsLoading;

    // CanDownload/CanMatchVolume depend on IsLoading, so every IsLoading transition must
    // re-notify them — not just the ones the load path happens to pass through.
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanMatchVolume));
    }

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
        LevelField = null;   // never leave a stale reference across a reload
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
            LevelField = levelField;
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
        OnPropertyChanged(nameof(CanMatchVolume));   // LevelField may have just been set or cleared
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

        // The device catalog moved (amp/IR added, deleted, or re-uploaded) — a cached blob from
        // MatchVolumeAsync could otherwise go on serving stale content under a reused name.
        _blobCache.Clear();

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

    /// <summary>Propose a Preset Level that makes THIS preset as loud as another one.
    ///
    /// Sets the slider and leaves it dirty rather than writing: the user reviews the number and
    /// presses Save, exactly like every other parameter in this panel. The estimate is offline
    /// (see Sonulab.Distill.LevelModel) — its amp-model term is exact, and everything it cannot
    /// derive is reported to the user rather than silently folded in.</summary>
    [RelayCommand]
    public async Task MatchVolumeAsync(Func<Task<int?>> pickTarget)
    {
        if (LevelField is null || _readAmpBlob is null || _readPresetDoc is null) return;

        int? target = await pickTarget();
        if (target is not { } targetIndex) return;

        ErrorMessage = null;
        using var op = _status.BeginOperation("Matching volume…");
        try
        {
            var mine = await EstimateLoadedAsync();
            var theirs = await EstimateSlotAsync(targetIndex);

            double proposed = theirs.Estimate.RelativeLufs + theirs.Estimate.CurrentTrimDb - mine.Estimate.RelativeLufs;
            double clamped = Math.Clamp(proposed, LevelField.Min, LevelField.Max);
            LevelField.Number = clamped;

            var notes = new List<string>();
            if (Math.Abs(clamped - proposed) > 1e-6)
                notes.Add($"that's as far as it goes ({proposed:F1} dB needed)");
            // The assumed amp-Volume taper cancels out of the difference ONLY when both presets
            // sit at the same amp\vol — two different off-default values do NOT cancel, even
            // though each one individually looks "off default" to the flag. So this compares the
            // actual values, not whether each side happens to raise the flag.
            bool ampVolCancels = Math.Abs(mine.AmpVolPercent - theirs.AmpVolPercent) <= 1e-9;
            foreach (var f in mine.Estimate.Unmodeled.Concat(theirs.Estimate.Unmodeled).Distinct(StringComparer.Ordinal))
                if (f != Sonulab.Distill.LevelModel.AmpVolFlag || !ampVolCancels)
                    notes.Add(f);

            _status.Success(notes.Count == 0
                ? $"Preset Level set to {clamped:F1} dB — Save to apply"
                : $"Preset Level set to {clamped:F1} dB — Save to apply. Check by ear: {string.Join("; ", notes)}");
        }
        catch (Exception ex)
        {
            // [RelayCommand] async: an escape here is an unhandled UI-thread rethrow.
            Log.Warn(ex, "volume match against slot {0} failed", targetIndex);
            ErrorMessage = $"Match failed: {ex.Message}";
            _status.Failure($"Match failed: {ex.Message}");
        }
    }

    /// <summary>A <see cref="Sonulab.Distill.PresetLevelEstimate"/> plus the actual amp\vol PERCENT
    /// it was computed from. Carried alongside the estimate — rather than re-derived from its
    /// `Unmodeled` flags — so <see cref="MatchVolumeAsync"/> can tell whether the assumed taper
    /// truly cancels between two presets (their values are equal) instead of guessing from
    /// whether each one individually happens to differ from ITS OWN default.</summary>
    private readonly record struct SideEstimate(Sonulab.Distill.PresetLevelEstimate Estimate, double AmpVolPercent);

    /// <summary>Estimate the preset currently in the editor, using the live field values rather
    /// than re-reading the slot — the editor already holds them, including unsaved edits, which
    /// is what the user is actually listening to.</summary>
    private async Task<SideEstimate> EstimateLoadedAsync()
    {
        var byPath = AllFields().ToDictionary(f => f.Path, f => f.ToJsonValue(), StringComparer.Ordinal);
        return await EstimateAsync(byPath);
    }

    private async Task<SideEstimate> EstimateSlotAsync(int index)
    {
        var doc = await _readPresetDoc!(index, CancellationToken.None);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Sonulab.Distill.LevelModel.InputPaths)
            if (doc.GetValueJson(p) is { } v) byPath[p] = v;
        return await EstimateAsync(byPath);
    }

    /// <summary>Resolve the preset's named amp and IRs to slot blobs, then run the model.</summary>
    private async Task<SideEstimate> EstimateAsync(IReadOnlyDictionary<string, string> byPath)
    {
        byte[] amp = await BlobForAsync(@"root\amp", byPath, Sonulab.Distill.LevelModel.AmpNamePath,
                                        _readAmpBlob!) ?? Array.Empty<byte>();
        byte[]? ir1 = await BlobForAsync(@"root\ir", byPath, Sonulab.Distill.LevelModel.IrNamePath,
                                         async (i, ct) => await _readIrBlob!(i, ct) ?? Array.Empty<byte>());
        byte[]? ir2 = await BlobForAsync(@"root\ir", byPath, Sonulab.Distill.LevelModel.Ir2NamePath,
                                         async (i, ct) => await _readIrBlob!(i, ct) ?? Array.Empty<byte>());

        var defaults = AllFields()
            .Where(f => f.Default is not null)
            .ToDictionary(f => f.Path, f => f.Default!.Value, StringComparer.Ordinal);

        var estimate = Sonulab.Distill.LevelModel.Estimate(byPath, amp, ir1, ir2, defaults);

        // Same fallback LevelModel.Estimate itself uses for a missing amp\vol node, so this can
        // never disagree with what the model actually computed the estimate against.
        double ampVolPercent = byPath.TryGetValue(Sonulab.Distill.LevelModel.AmpVolPath, out var raw)
            && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : Sonulab.Distill.LevelModel.AmpVolReferencePercent;

        return new SideEstimate(estimate, ampVolPercent);
    }

    /// <summary>A preset names its amp/IR by NAME, so resolve the name against the device list to
    /// get a slot, then read that slot's blob. Returns null when the preset names nothing, the
    /// name is not on the device (an orphaned reference), or no reader was supplied — the model
    /// flags those cases rather than failing.
    ///
    /// Blobs are memoized per view-model instance: a 96-chunk amp read is ~3 s, and both sides of
    /// a comparison usually name the same amp. NOT persisted across sessions — the model needs the
    /// blob itself, so a cached scalar could not short-circuit it (see the spec correction above).
    /// </summary>
    private readonly Dictionary<string, byte[]> _blobCache = new(StringComparer.Ordinal);

    private async Task<byte[]?> BlobForAsync(string listPath,
        IReadOnlyDictionary<string, string> byPath, string namePath,
        Func<int, CancellationToken, Task<byte[]>>? read)
    {
        if (read is null) return null;
        string name = Unquote(byPath.GetValueOrDefault(namePath, ""));
        if (name.Length == 0) return null;

        string key = listPath + "|" + name;
        if (_blobCache.TryGetValue(key, out var cached)) return cached;

        var names = await _client.ReadListAsync(listPath);
        int slot = -1;
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal)) { slot = i; break; }
        if (slot < 0) return null;

        var blob = await read(slot, CancellationToken.None);
        _blobCache[key] = blob;
        return blob;
    }

    private static string Unquote(string json) => json.Trim().Trim('"');

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
