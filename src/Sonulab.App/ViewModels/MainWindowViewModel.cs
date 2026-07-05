using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

namespace Sonulab.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private bool _ampsLoaded, _irsLoaded;

    [ObservableProperty] private ConnectionViewModel _connection;
    [ObservableProperty] private PresetListViewModel? _presets;
    [ObservableProperty] private ParameterEditorViewModel? _editor;
    [ObservableProperty] private AmpListViewModel? _amps;
    [ObservableProperty] private IrListViewModel? _irs;

    /// <summary>Set by the view on every nav change; lets the Connected handler lazy-load
    /// whichever tab the user is already looking at.</summary>
    public int CurrentNavIndex { get; set; }

    /// <summary>Lazy tab loading (perf spec §3): Amps/IRs fetch their device lists on FIRST
    /// visit instead of at connect — removes two full list reads from the connect path.</summary>
    public void EnsureTabLoaded(int navIndex)
    {
        if (navIndex == 1 && Amps is { } a && !_ampsLoaded) { _ampsLoaded = true; _ = TimedRefreshAsync(a.RefreshCommand, "amps-first-visit"); }
        else if (navIndex == 2 && Irs is { } i && !_irsLoaded) { _irsLoaded = true; _ = TimedRefreshAsync(i.RefreshCommand, "irs-first-visit"); }
    }

    private static async Task TimedRefreshAsync(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand refresh, string label)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await refresh.ExecuteAsync(null);
        Log.Info("PERF {0}={1}ms", label, sw.ElapsedMilliseconds);
    }

    public MainWindowViewModel()
    {
        // Adaptive settle (perf spec §4): instead of always paying a fixed 1500 ms for the
        // ESP32's post-open reboot, wait briefly and let the probe-retry loop find the moment
        // the device answers. Worst case ≈ 250 + 8×(300 fail-fast + 150 delay) ≈ 3.9 s (old:
        // 1500 + 3×(300+300) ≈ 3.3 s); typical case = actual boot time + ≤450 ms overshoot.
        var options = new SerialLinkOptions
        { OpenSettleMs = 250, ProbeAttempts = 8, ProbeRetryDelayMs = 150 };
        var connector = new SonuConnector(() => new SystemSerialPort(), options);
        var session = new DeviceSession(connector, new CompatibilityChecker(FirmwareCatalog.Default));

        var ports = System.IO.Ports.SerialPort.GetPortNames();
        var portList = (ports.Length > 0 ? ports : new[] { "COM6" }) as IReadOnlyList<string>;

        _connection = new ConnectionViewModel(session, portList);
        _connection.Connected += (_, _) =>
        {
            _ampsLoaded = _irsLoaded = false;

            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed);
            var editor = new ParameterEditorViewModel(_connection.Client!);
            // Selecting a preset activates + loads it into the editor (dedup is handled in LoadForAsync).
            presets.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PresetListViewModel.Selected)
                    && presets.Selected is { IsEmpty: false } sel)
                    editor.LoadForCommand.Execute(sel.Name);
            };
            Presets = presets;
            Editor = editor;

            var ampService = new AmpService(
                _connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed);
            Amps = amps;

            var irService = new IrService(_connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var irs = new IrListViewModel(irService, _connection.WritesAllowed);
            Irs = irs;

            _ = LoadInitialAsync(presets);
            EnsureTabLoaded(CurrentNavIndex);
        };

        async Task LoadInitialAsync(PresetListViewModel presets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await presets.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect presets-list={0}ms", sw.ElapsedMilliseconds);
        }
    }
}
