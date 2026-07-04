using CommunityToolkit.Mvvm.ComponentModel;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

namespace Sonulab.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    [ObservableProperty] private ConnectionViewModel _connection;
    [ObservableProperty] private PresetListViewModel? _presets;
    [ObservableProperty] private ParameterEditorViewModel? _editor;
    [ObservableProperty] private AmpListViewModel? _amps;
    [ObservableProperty] private IrListViewModel? _irs;

    public MainWindowViewModel()
    {
        var options = new SerialLinkOptions { OpenSettleMs = 1500, ProbeAttempts = 3 };
        var connector = new SonuConnector(() => new SystemSerialPort(), options);
        var session = new DeviceSession(connector, new CompatibilityChecker(FirmwareCatalog.Default));

        var ports = System.IO.Ports.SerialPort.GetPortNames();
        var portList = (ports.Length > 0 ? ports : new[] { "COM6" }) as IReadOnlyList<string>;

        _connection = new ConnectionViewModel(session, portList);
        _connection.Connected += (_, _) =>
        {
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

            _ = LoadInitialAsync(presets, amps, irs);
        };

        async Task LoadInitialAsync(PresetListViewModel presets, AmpListViewModel amps, IrListViewModel irs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await presets.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect presets-list={0}ms", sw.ElapsedMilliseconds);
            sw.Restart();
            await amps.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect amps-list={0}ms", sw.ElapsedMilliseconds);
            sw.Restart();
            await irs.RefreshCommand.ExecuteAsync(null);
            Log.Info("PERF connect irs-list={0}ms", sw.ElapsedMilliseconds);
        }
    }
}
