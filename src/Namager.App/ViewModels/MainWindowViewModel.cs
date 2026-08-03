using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Namager.App.Services;
using Sonulab.Core.Connection;
using Sonulab.Core.Model;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

namespace Namager.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, Namager.App.Services.IPresetNavigator
{
    /// <summary>#14: jump from an amp's "used in presets" list to the preset itself. Selecting it
    /// ACTIVATES it on the pedal, exactly as clicking it in the preset list does — the same
    /// single-flight path added for #10, so a burst of clicks cannot desync the device.</summary>
    public void NavigateToPreset(int index, string name)
    {
        NavigateRequested?.Invoke(0);                       // Presets tab
        if (Presets is { } p)
            p.Selected = p.Items.FirstOrDefault(i => i.Index == index);
    }

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private bool _ampsLoaded, _irsLoaded;
    private Namager.App.Services.PresetUsageService? _usageService;
    private Namager.App.Services.CatalogVersion? _catalog;

    /// <summary>The single status/progress channel, bound by the bottom status bar and shared
    /// with every child VM (passed via their constructors). Created once, lives for the app.</summary>
    public StatusService Status { get; } = new();

    [ObservableProperty] private ConnectionViewModel _connection;
    [ObservableProperty] private PresetListViewModel? _presets;
    [ObservableProperty] private ParameterEditorViewModel? _editor;
    [ObservableProperty] private AmpListViewModel? _amps;
    [ObservableProperty] private IrListViewModel? _irs;
    [ObservableProperty] private Tone3000ViewModel _tone3000;
    [ObservableProperty] private UpdateInfo? _updateAvailable;

    /// <summary>True for the whole duration of a Backup or a Restore run. Restore's progress
    /// dialog is modal, so it accidentally already blocked overlap — but Backup shows no dialog
    /// (just an indeterminate status-bar operation), so without this flag the user could start a
    /// second Backup, or a Restore, while one was already in flight. SonuClient serialises the
    /// wire so nothing corrupts on the device, but a Restore interleaved with a Backup would
    /// silently mix pre- and post-restore slots into one backup folder.</summary>
    [ObservableProperty] private bool _fileOperationInFlight;

    partial void OnFileOperationInFlightChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CanRestore));
    }

    /// <summary>Backup only reads the pedal, so — unlike Restore — it never needs
    /// <see cref="Sonulab.Core.Connection"/>'s write gate.</summary>
    public bool CanBackup => Connection.IsConnected && !FileOperationInFlight;

    /// <summary>Restore writes to the device, so it is additionally gated on WritesAllowed: on
    /// firmware the compatibility checker has not verified for writes, Restore must stay disabled
    /// rather than let the user click through a confirmation dialog only to be rejected.</summary>
    public bool CanRestore => Connection.IsConnected && Connection.WritesAllowed && !FileOperationInFlight;

    // The generated Connection setter (object-initializer / explicit assignment) runs this, but
    // the constructor below assigns the backing field directly to avoid a partially-constructed
    // ConnectionViewModel racing its own PropertyChanged handlers — so it calls HookConnection
    // itself right after construction. Either path ends up subscribed exactly once per instance.
    partial void OnConnectionChanged(ConnectionViewModel value) => HookConnection(value);

    /// <summary>CanBackup/CanRestore read Connection.IsConnected/WritesAllowed live, so they are
    /// always correct once read — this subscription exists purely to fire PropertyChanged so
    /// bound UI (MenuItem.IsEnabled) re-reads them.</summary>
    private void HookConnection(ConnectionViewModel connection) => connection.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected) or nameof(ConnectionViewModel.WritesAllowed))
        {
            OnPropertyChanged(nameof(CanBackup));
            OnPropertyChanged(nameof(CanRestore));
        }
    };

    /// <summary>The persisted theme choice: "System", "Light", or "Dark".</summary>
    [ObservableProperty] private string _theme = Namager.App.Services.ThemeSettings.System;

    public bool IsThemeSystem => Theme == Namager.App.Services.ThemeSettings.System;
    public bool IsThemeLight => Theme == Namager.App.Services.ThemeSettings.Light;
    public bool IsThemeDark => Theme == Namager.App.Services.ThemeSettings.Dark;

    partial void OnThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    /// <summary>Apply and persist a theme choice. Applying is immediate — Avalonia re-resolves the
    /// theme dictionaries, and every colour in this app comes from a token with Light and Dark
    /// variants, so no restart is needed.</summary>
    [RelayCommand]
    private void SetTheme(string? theme)
    {
        if (theme is not (Namager.App.Services.ThemeSettings.System
                       or Namager.App.Services.ThemeSettings.Light
                       or Namager.App.Services.ThemeSettings.Dark)) return;
        Theme = theme;
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = Namager.App.Services.ThemeSettings.ToVariant(theme);
        // Preserve ShareUsageData: a fresh `new AppSettings { Theme = theme }` would default it
        // back to true and silently undo an opt-out the next time the user picks a theme.
        Namager.App.Services.AppSettingsStore.Save(
            new Namager.App.Services.AppSettings { Theme = theme, ShareUsageData = ShareUsageData },
            _settingsPath);
    }

    /// <summary>Whether the anonymous connect ping is sent (PRIVACY.md section 1). Loaded from
    /// settings at startup below and persisted here — the same round trip as <see cref="Theme"/>.
    /// The next ping reads the freshly saved value straight off disk via UsagePingService's own
    /// default (AppSettingsStore.Load().ShareUsageData), so no in-memory wiring is needed to make
    /// a toggle mid-session take effect.</summary>
    [ObservableProperty] private bool _shareUsageData = true;

    /// <summary>The Settings menu's checkbox item has no CommandParameter to carry a target value
    /// (unlike the Theme radios), so it toggles the current value instead — bound the same way,
    /// one-way IsChecked plus a Command that persists via AppSettingsStore.</summary>
    [RelayCommand]
    private void ToggleShareUsageData()
    {
        ShareUsageData = !ShareUsageData;
        Namager.App.Services.AppSettingsStore.Save(
            new Namager.App.Services.AppSettings { Theme = Theme, ShareUsageData = ShareUsageData },
            _settingsPath);
    }

    /// <summary>Raised when a flow needs the window to switch nav tabs (index into NavList).</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>Set by the view on every nav change; lets the Connected handler lazy-load
    /// whichever tab the user is already looking at.</summary>
    public int CurrentNavIndex { get; set; }

    /// <summary>Test/handoff seam: the in-flight first-visit refresh kicked off by
    /// <see cref="EnsureTabLoaded"/> (if any). <see cref="NavigateToUploadAsync"/> awaits this
    /// before prefilling, so a Tone3000 send-to-pedal that arrives before the target tab has ever
    /// been visited doesn't race the lazy load (C1).</summary>
    public Task? PendingTabLoad { get; private set; }

    /// <summary>Lazy tab loading (perf spec §3): Amps/IRs fetch their device lists on FIRST
    /// visit instead of at connect — removes two full list reads from the connect path.</summary>
    public void EnsureTabLoaded(int navIndex)
    {
        if (navIndex == 0 && Editor is { } ed)
        {
            // Returning to the Presets tab is the moment stale amp/IR pickers become visible.
            // Cheap and idempotent: a no-op unless an amp/IR was added or removed since the load.
            PendingTabLoad = ed.RefreshRefOptionsAsync();
        }
        else if (navIndex == 1 && Amps is { } a)
        {
            if (!_ampsLoaded) { _ampsLoaded = true; PendingTabLoad = TimedRefreshAsync(a.RefreshCommand, "amps-first-visit"); }
            else PendingTabLoad = a.RefreshUsageAsync();   // revisit: refresh "used" highlights only
        }
        else if (navIndex == 2 && Irs is { } i)
        {
            if (!_irsLoaded) { _irsLoaded = true; PendingTabLoad = TimedRefreshAsync(i.RefreshCommand, "irs-first-visit"); }
            else PendingTabLoad = i.RefreshUsageAsync();
        }
    }

    private static async Task TimedRefreshAsync(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand refresh, string label)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await refresh.ExecuteAsync(null);
        Log.Info("PERF {0}={1}ms", label, sw.ElapsedMilliseconds);
    }

    /// <summary>Called by the view once the window has opened (fire-and-forget there).
    /// Seam: tests pass a fake; the view passes a real UpdateCheckService.</summary>
    public async Task CheckForUpdatesAsync(IUpdateCheckService service)
        => UpdateAvailable = await service.CheckAsync();

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = null;

    /// <summary>Where the theme preference is read from and written to. Null = the real
    /// %APPDATA%\Namager\settings.json. Tests pass a temp path — a test that saves settings must
    /// never touch the developer's own preferences.</summary>
    private readonly string? _settingsPath;

    /// <summary>Where the Tone3000 IR identity index is read from and written to by Export/Import
    /// Snapshot. Null = the real %APPDATA%\Namager\ir-index.json (matches IrIndex.DefaultPath and
    /// how IrListViewModel takes the same parameter). Tests pass a temp path — a test that imports
    /// or exports a snapshot must never touch the developer's own index.</summary>
    private readonly string? _irIndexPath;

    /// <summary>Where the preset-usage warm-start cache is read from and written to. Null = the
    /// real %APPDATA%\Namager\preset-usage-cache.json (PresetUsageCache.DefaultPath). Tests pass
    /// a temp path — a test that connects must never touch the developer's own cache.</summary>
    private readonly string? _usageCachePath;

    public MainWindowViewModel() : this(null, null, null) { }

    public MainWindowViewModel(string? settingsPath) : this(settingsPath, null, null) { }

    public MainWindowViewModel(string? settingsPath, string? irIndexPath)
        : this(settingsPath, irIndexPath, null) { }

    public MainWindowViewModel(string? settingsPath, string? irIndexPath, string? usageCachePath)
    {
        _settingsPath = settingsPath;
        _irIndexPath = irIndexPath;
        _usageCachePath = usageCachePath;
        // Tone3000 (Browse Tones) exists from startup - browsing needs no pedal. Null config
        // = the tab shows its "add your keys" card.
        var t3kConfig = Namager.Tone3000.T3kConfig.TryLoad();
        if (t3kConfig is not null)
        {
            var t3kAuth = new Namager.Tone3000.T3kAuth(t3kConfig, new Namager.Tone3000.T3kTokenStore());
            _tone3000 = new Tone3000ViewModel(t3kAuth, new Namager.Tone3000.T3kClient(t3kAuth),
                new Namager.Tone3000.T3kDownloader(t3kAuth,
                    System.IO.Path.Combine(AppContext.BaseDirectory, "NAMFiles", "Tone3000")));
        }
        else
        {
            _tone3000 = new Tone3000ViewModel(null, null, null);
        }
        _tone3000.SendToPedalRequested += (path, notes, url, isIr, irSource) =>
            NavigateToUpload(isIr, path, notes, url, irSource);   // fire-and-forget wrapper; keeps this subscription simple

        // Adaptive settle (perf spec §4): instead of always paying a fixed 1500 ms for the
        // ESP32's post-open reboot, wait briefly and let the probe-retry loop find the moment
        // the device answers. Worst case: 250 + 8×300 + 7×150 ≈ 3.7s (old: 1500 + 3×300 + 2×300
        // ≈ 3.0s); typical case = actual boot time + ≤450 ms overshoot.
        // Accepted trade-off: a silent or chatty NON-pedal port scans slower than before (worst
        // case ~21s if a port streams data without NUL terminators); on this bench the pedal is
        // the only serial device.
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
        var session = new DeviceSession(BuildProviders(options), new CompatibilityChecker(FirmwareCatalog.Default));

        _connection = new ConnectionViewModel(session, new UsagePingService(), Status);
        // Direct field assignment above bypasses the generated property setter (and so
        // OnConnectionChanged) — hook it explicitly so CanBackup/CanRestore stay live.
        HookConnection(_connection);
        // The scan's link is dead; its task must not keep polling a corpse. (SonuClient's latch
        // makes each attempt fail instantly, so this is about ending it, not about cost.)
        _connection.DeviceLost += (_, _) => _usageService?.Stop();
        _connection.Connected += (_, _) =>
        {
            _ampsLoaded = _irsLoaded = false;

            // One scanner per connection. Stop the previous connection's background scan first —
            // its link is gone and its task must not linger into the new session.
            _usageService?.Stop();
            // Device id keys the warm-start cache; a blank id (unknown firmware) disables it.
            var usage = _usageService = new PresetUsageService(
                _connection.Repository!, _connection.DeviceId, _usageCachePath);
            var catalog = _catalog = new Namager.App.Services.CatalogVersion();

            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed,
                Status,
                usage);
            // Absolute, under Documents: the old relative "docs\backups" resolved against the
            // process working directory, so in an installed build these landed where no user would
            // look. Only upload writes here now — delete archives nothing. See AppPaths.
            var slotBackups = Namager.App.Services.AppPaths.SlotBackups;
            // Built before the editor: the editor's amp-detail flyout (#9) reads metadata through
            // the same service and the same region-read helper the Amps tab uses.
            var ampService = new AmpService(_connection.Client!, slotBackups);

            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status,
                repo: _connection.Repository!, usage: usage, catalog: catalog,
                readAmpMetadata: (i, ct) => AmpListViewModel.ReadMetadataAsync(ampService, i, ct),
                navigator: this);
            // Selecting a preset activates + loads it into the editor (dedup is handled in LoadForAsync).
            presets.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PresetListViewModel.Selected)
                    && presets.Selected is { IsEmpty: false } sel)
                    editor.LoadForCommand.Execute(new PresetTarget(sel.Index, sel.Name));
            };
            Presets = presets;
            Editor = editor;

            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed, Status, usage: usage, catalog: catalog, navigator: this);
            Amps = amps;

            var irService = new IrService(_connection.Client!, slotBackups);
            var irs = new IrListViewModel(irService, _connection.WritesAllowed, Status, usage: usage,
                catalog: catalog, irIndexPath: _irIndexPath);
            Irs = irs;

            Tone3000.IsDeviceReady = _connection.WritesAllowed;

            _ = LoadInitialAsync(presets);
            EnsureTabLoaded(CurrentNavIndex);
        };

        async Task LoadInitialAsync(PresetListViewModel presets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await presets.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect presets-list={0}ms", sw.ElapsedMilliseconds);
        }

        var loadedSettings = Namager.App.Services.AppSettingsStore.Load(_settingsPath);
        _theme = loadedSettings.Theme;
        _shareUsageData = loadedSettings.ShareUsageData;

        Status.SetIdleSummary("Not connected");
    }

    /// <summary>The transports the app will try, in order. USB ONLY — the WiFi/TCP transport
    /// (src/Sonulab.Transport.Wifi) still exists and is exercised by HwCheck's wifi mode and its own
    /// tests, but it is deliberately not offered to users: the pedal answers mDNS intermittently,
    /// the link needs a bespoke response-debt resync layer, and a pedal that vanishes without a
    /// FIN/RST produces no disconnect signal at all. See
    /// docs/superpowers/specs/2026-07-25-disable-wifi-in-app-design.md.
    ///
    /// Extracted from the constructor so LinkProviderWiringTests can assert the list stays
    /// single-entry — re-adding a fallback should fail a test, not slip through review.</summary>
    internal static IReadOnlyList<ILinkProvider> BuildProviders(SerialLinkOptions options) => new List<ILinkProvider>
    {
        // Fresh port enumeration per connect: a pedal replugged onto a new COM number
        // is found without restarting the app.
        new SerialLinkProvider(() => new SystemSerialPort(), options),
    };

    /// <summary>Tone3000 handoff: switch to the Amps or IRs tab and open the upload panel
    /// prefilled. Nav indices match MainWindow's NavList (0 presets, 1 amps, 2 irs, 4 t3k).
    /// Fire-and-forget wrapper around <see cref="NavigateToUploadAsync"/> so the Tone3000
    /// event subscription stays a plain synchronous handler.</summary>
    public void NavigateToUpload(bool isIr, string path, string? notes, string? url, T3kIrSource? irSource = null) =>
        _ = NavigateToUploadAsync(isIr, path, notes, url, irSource);

    /// <summary>C1: if the target tab has never been visited since connect, NavigateRequested
    /// triggers EnsureTabLoaded's first-visit refresh (View's nav-changed handler calls it
    /// synchronously off NavigateRequested). Awaiting that in-flight load before prefilling stops
    /// BeginUploadPrefilled from no-opping while Amps.IsBusy (CanMutate false) — or the IRs path
    /// seeing an empty Items list and reporting "no empty slots" wrongly.</summary>
    public async Task NavigateToUploadAsync(bool isIr, string path, string? notes, string? url,
                                            T3kIrSource? irSource = null)
    {
        if (isIr)
        {
            if (Irs is not { } irs) { Tone3000.Banner = "Connect to the pedal first."; return; }
            NavigateRequested?.Invoke(2);
            if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load; proceed anyway */ } }
            // IRs: identity comes from Tone3000 when present, else name prefill via filename only.
            if (irSource is { } src) irs.BeginUploadFromTone3000(path, src);
            else irs.BeginUploadCommand.Execute(path);
        }
        else
        {
            if (Amps is not { } amps) { Tone3000.Banner = "Connect to the pedal first."; return; }
            NavigateRequested?.Invoke(1);
            if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load; proceed anyway */ } }
            amps.BeginUploadPrefilled(path, notes, url);
        }
    }

    /// <summary>Where one backup run writes: Documents\NAMager Backups\yyyy-MM-dd HHmmss. A fresh
    /// timestamped folder per run (second resolution, so two clicks in the same minute still land
    /// in different folders), so a backup can never overwrite an earlier one and there is no
    /// destination to prompt for. `now` is a parameter so the format is testable.</summary>
    public static string NewBackupFolder(System.DateTime now) => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments),
        "NAMager Backups", now.ToString("yyyy-MM-dd HHmmss"));

    /// <summary>How a backup run turned out — the view uses it to offer "Open Folder".</summary>
    public sealed record BackupResult(int Count, string Folder);

    /// <summary>Snapshot every occupied preset to a new timestamped folder. Returns null when
    /// there is no connection or the run failed (the status bar carries the reason). Never throws.
    /// FileOperationInFlight is held for the whole call (including the disconnected early-return)
    /// so a concurrent Restore is blocked for the entire window a backup folder could be mid-write —
    /// Backup shows no modal dialog, so nothing else stops the user from starting a second
    /// operation while this one runs.</summary>
    public async Task<BackupResult?> BackupPresetsAsync()
    {
        FileOperationInFlight = true;
        try
        {
            if (Connection.Repository is not { } repo) return null;
            try
            {
                var folder = NewBackupFolder(System.DateTime.Now);
                using var op = Status.BeginOperation("Backing up presets…");
                int n = await new Sonulab.Core.Services.BackupService(repo).SnapshotAllAsync(folder);
                Status.Success($"Backed up {n} preset{(n == 1 ? "" : "s")}.");
                return new BackupResult(n, folder);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "preset backup failed");
                Status.Failure($"Backup failed: {ex.Message}");
                return null;
            }
        }
        finally { FileOperationInFlight = false; }
    }

    /// <summary>Write each planned file to its slot, in slot order, reporting "n/m — Name" through
    /// <paramref name="progress"/> and honouring cancellation BETWEEN slots (a chunked write is
    /// never interrupted mid-flight — that would leave a torn preset). Roughly 10 s per slot.
    /// A per-slot failure is counted and the run continues; the summary says how many.
    /// Returns the human-readable summary. Never throws.</summary>
    public async Task<string> RestorePresetsAsync(
        Namager.App.Services.RestorePlan plan,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        FileOperationInFlight = true;
        try
        {
            if (Connection.Repository is not { } repo)
                return "Not connected — nothing was restored.";
            // Split from the "not connected" case above: this is a real, reachable state (the
            // pedal IS connected) and telling the user they aren't connected would be a lie — they
            // just clicked through a confirmation naming exact slots and a duration.
            if (!Connection.WritesAllowed)
                return "This firmware isn't verified for writes — nothing was restored.";

            var backup = new Sonulab.Core.Services.BackupService(repo);
            int done = 0, failed = 0;
            using var op = Status.BeginOperation("Restoring presets…", determinate: true);
            foreach (var item in plan.Items)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report($"{done + failed + 1}/{plan.Items.Count} — {item.Name}");
                op.Report($"Restoring {done + failed + 1}/{plan.Items.Count}: {item.Name}");
                // Deliberately CancellationToken.None below, NOT ct: DeviceRepository.WritePresetToSlotAsync
                // is rename -> per-param live replay -> save-by-name -> verify, four independently-awaited
                // device round trips, not one atomic operation. Forwarding `ct` into it lets cancellation
                // land between, e.g., the rename and the save, leaving the slot renamed to the new preset
                // while still holding the OLD preset's content — a silent corrupt slot, not a visible
                // failure. Once a slot's restore has started it must run to completion; `ct` is checked
                // only at the top of this loop, so cancellation takes effect after the current preset
                // finishes (matching the progress dialog's "Cancelling — finishing the current preset…").
                try { await backup.RestoreSlotAsync(item.Index, item.Path, CancellationToken.None); done++; }
                catch (OperationCanceledException) { break; } // defensive: should not fire given the above
                catch (Exception ex)
                {
                    Log.Warn(ex, "restore of slot {0} failed", item.Index);
                    failed++;
                }
                op.Report((double)(done + failed) / plan.Items.Count);
            }

            // Many slots changed at once — a full rescan is the honest choice here, not a patch.
            _usageService?.Invalidate();
            if (Presets is { } p) { try { await p.RefreshCommand.ExecuteAsync(null); } catch { /* list resync is best effort */ } }

            var summary = $"Restored {done} preset{(done == 1 ? "" : "s")}"
                        + (failed > 0 ? $", {failed} failed" : "")
                        + (plan.Skipped.Count > 0 ? $", {plan.Skipped.Count} skipped" : "") + ".";
            if (failed > 0) Status.Failure(summary); else Status.Success(summary);
            return summary;
        }
        finally { FileOperationInFlight = false; }
    }

    /// <summary>Writes a .namsnap of every occupied preset/amp/IR to <paramref name="path"/>.
    /// Read-only against the device — never writes to it. Never throws; a missing connection or any
    /// failure mid-capture is routed to Status, mirroring BackupPresetsAsync's shape.
    ///
    /// ATOMICITY: SnapshotArchive.Write is not atomic on the stream it is given (see its own
    /// ATOMICITY note) — a fault partway through leaves a partial-but-openable ZIP on whatever
    /// stream it was writing. This method therefore captures into a temp file created beside
    /// <paramref name="path"/> and renames it onto <paramref name="path"/> only after the capture
    /// returns successfully. A disk fault (or a device disconnect) mid-export must not destroy a
    /// pre-existing good backup at <paramref name="path"/> while failing to produce a new one.</summary>
    public async Task ExportSnapshotAsync(string path)
    {
        FileOperationInFlight = true;
        try
        {
            if (Connection.Repository is not { } repo)
            {
                Status.Failure("Connect to the pedal first.");
                return;
            }

            using var op = Status.BeginOperation("Exporting snapshot…");
            // Same directory as the destination, so the final move below is an in-place rename
            // (not a cross-volume copy that could itself fail partway).
            var tempPath = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var slotBackups = AppPaths.SlotBackups;
                var svc = new SnapshotService(
                    repo,
                    new AmpService(Connection.Client!, slotBackups),
                    new IrService(Connection.Client!, slotBackups));
                var index = IrIndex.Load(_irIndexPath);

                await using (var file = File.Create(tempPath))
                {
                    // CancellationToken.None, deliberately: a full pedal is 30 presets + 30 amps +
                    // 30 IRs = 5760 chunks at ~33 ms/chunk (SonuClient.DReadChunkRangeAsync) —
                    // roughly three minutes with no way to stop it. That is a known limitation of
                    // this release, not an oversight: adding a cancel button is scope growth here
                    // (see the fix-wave report), and the user isn't left blind while it runs —
                    // op.Report below drives the status bar with live per-slot progress.
                    await svc.CaptureAsync(
                        file,
                        new SnapshotDevice("StompStation", Connection.FirmwareVersion ?? "unknown"),
                        Namager.App.AppInfo.Version,
                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        resolveIrIdentity: blob => index.Lookup(IrIndex.ShaOf(blob)) is { } e
                            ? new SnapshotT3k(e.ToneId, e.ModelId)
                            : null,
                        // Done is the capture-wide file counter, so the message must say so —
                        // "Presets 31/81" read as "preset 31 of 81 presets" when only 30 exist.
                        progress: new Progress<SnapshotCaptureProgress>(p => op.Report(p.Stage switch
                        {
                            SnapshotSlotKind.Preset => $"Saving Preset files from pedal first — #{p.Done} of {p.Total} total files",
                            SnapshotSlotKind.Amp => $"Saving Amp files from pedal next — #{p.Done} of {p.Total} total files",
                            _ => $"Saving IR files from pedal last — #{p.Done} of {p.Total} total files",
                        })),
                        ct: CancellationToken.None);
                }

                // Only touch the real destination once the temp file is a complete, valid archive.
                File.Move(tempPath, path, overwrite: true);
                Status.Success($"Snapshot written to {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                // The real destination was never opened for writing above, so it is untouched
                // regardless of where the capture failed — only the temp file needs cleanup.
                try { File.Delete(tempPath); } catch { /* best effort; a stray temp file costs disk space, not correctness */ }
                Log.Warn(ex, "snapshot export failed");
                Status.Failure($"Export failed: {ex.Message}");
            }
        }
        finally { FileOperationInFlight = false; }
    }

    /// <summary>Reads and validates a .namsnap, and records any IR Tone3000 identities it carries
    /// into the local IR index. Does NOT write anything to the pedal — restoring a snapshot onto
    /// hardware is a deliberately separate, not-yet-built feature (it needs selective slot choice,
    /// a resumable multi-minute write, and cross-device rules).
    ///
    /// Unlike ExportSnapshotAsync, a validation failure here is NOT swallowed: it propagates as
    /// SnapshotArchiveException so the file-picker handler can show the exact reason the file was
    /// rejected, rather than a generic status-bar message.</summary>
    public async Task<SnapshotManifest> ImportSnapshotAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var (manifest, blobs) = SnapshotArchive.Read(file);

        var index = IrIndex.Load(_irIndexPath);
        int learned = 0;
        foreach (var slot in manifest.Slots.Where(s => s.Kind == SnapshotSlotKind.Ir && s.T3k is not null))
        {
            var blob = blobs[(SnapshotSlotKind.Ir, slot.Index)];
            // Title: null, not slot.Name. slot.Name is the device slot name — exactly the
            // user-renamed string the index exists to see past (IrListViewModel's own writer uses
            // the real Tone3000 tone title instead). SnapshotT3k carries no title field, so import
            // has no way to recover the real one; recording the slot name here would just put the
            // renamed string back into the field meant to escape it.
            index = index.Record(new IrIndexEntry(
                IrIndex.ShaOf(blob), slot.T3k!.ToneId, slot.T3k.ModelId, Title: null));
            learned++;
        }
        index.Save(_irIndexPath);

        // Same per-kind vocabulary as the export progress line. Import is a local file read
        // (nothing is written to the pedal), so it finishes too fast for per-file progress —
        // the breakdown lands in this one summary instead.
        int presets = manifest.Slots.Count(s => s.Kind == SnapshotSlotKind.Preset);
        int ampCount = manifest.Slots.Count(s => s.Kind == SnapshotSlotKind.Amp);
        int irCount = manifest.Slots.Count(s => s.Kind == SnapshotSlotKind.Ir);
        Status.Success(
            $"Read {manifest.Slots.Count} files from snapshot ({presets} Presets, {ampCount} Amps, {irCount} IRs) — " +
            $"learned {learned} IR identit{(learned == 1 ? "y" : "ies")}");
        return manifest;
    }
}
