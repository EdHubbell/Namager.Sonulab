using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Namager.App.Services;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

namespace Namager.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private bool _ampsLoaded, _irsLoaded;
    private Namager.App.Services.PresetUsageService? _usageService;

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
        if (navIndex == 1 && Amps is { } a)
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

    public MainWindowViewModel()
    {
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
        _tone3000.SendToPedalRequested += (path, notes, url, isIr) =>
            NavigateToUpload(isIr, path, notes, url);   // fire-and-forget wrapper; keeps this subscription simple

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
        // The scan's link is dead; its task must not keep polling a corpse. (SonuClient's latch
        // makes each attempt fail instantly, so this is about ending it, not about cost.)
        _connection.DeviceLost += (_, _) => _usageService?.Stop();
        _connection.Connected += (_, _) =>
        {
            _ampsLoaded = _irsLoaded = false;

            // One scanner per connection. Stop the previous connection's background scan first —
            // its link is gone and its task must not linger into the new session.
            _usageService?.Stop();
            var usage = _usageService = new PresetUsageService(_connection.Repository!);

            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed,
                Status,
                usage);
            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status,
                repo: _connection.Repository!, usage: usage);
            // Selecting a preset activates + loads it into the editor (dedup is handled in LoadForAsync).
            presets.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PresetListViewModel.Selected)
                    && presets.Selected is { IsEmpty: false } sel)
                    editor.LoadForCommand.Execute(new PresetTarget(sel.Index, sel.Name));
            };
            Presets = presets;
            Editor = editor;

            var ampService = new AmpService(
                _connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed, Status, usage: usage);
            Amps = amps;

            var irService = new IrService(_connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var irs = new IrListViewModel(irService, _connection.WritesAllowed, Status, usage: usage);
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
    public void NavigateToUpload(bool isIr, string path, string? notes, string? url) =>
        _ = NavigateToUploadAsync(isIr, path, notes, url);

    /// <summary>C1: if the target tab has never been visited since connect, NavigateRequested
    /// triggers EnsureTabLoaded's first-visit refresh (View's nav-changed handler calls it
    /// synchronously off NavigateRequested). Awaiting that in-flight load before prefilling stops
    /// BeginUploadPrefilled from no-opping while Amps.IsBusy (CanMutate false) — or the IRs path
    /// seeing an empty Items list and reporting "no empty slots" wrongly.</summary>
    public async Task NavigateToUploadAsync(bool isIr, string path, string? notes, string? url)
    {
        if (isIr)
        {
            if (Irs is not { } irs) { Tone3000.Banner = "Connect to the pedal first."; return; }
            NavigateRequested?.Invoke(2);
            if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load; proceed anyway */ } }
            irs.BeginUploadCommand.Execute(path);            // IRs: name prefill via filename; no SSMD
        }
        else
        {
            if (Amps is not { } amps) { Tone3000.Banner = "Connect to the pedal first."; return; }
            NavigateRequested?.Invoke(1);
            if (PendingTabLoad is { } t) { try { await t; } catch { /* superseded/failed load; proceed anyway */ } }
            amps.BeginUploadPrefilled(path, notes, url);
        }
    }
}
