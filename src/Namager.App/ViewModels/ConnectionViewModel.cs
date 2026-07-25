using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;
using Sonulab.Core.Transport;

namespace Namager.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly DeviceSession _session;
    private readonly Namager.App.Services.IUsagePingService? _usage;
    // Named _statusService (not _status) to avoid clashing with the [ObservableProperty] backing
    // field for the Status string property below.
    private readonly Namager.App.Services.IStatusService _statusService;
    private readonly Action<Action> _dispatch;
    private bool _usagePinged;   // first successful connect of this app run only

    public ConnectionViewModel(DeviceSession session,
                               Namager.App.Services.IUsagePingService? usage = null,
                               Namager.App.Services.IStatusService? status = null,
                               Action<Action>? dispatch = null)
    { _session = session; _usage = usage;
      _statusService = status ?? Namager.App.Services.NullStatusService.Instance;
      _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a)); }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private string _status = "Disconnected";

    /// <summary>Latched once the link dies mid-session. Keeps Connect disabled: re-opening the
    /// transport on a session whose link is gone is the reconnect path this design rejects
    /// (see CanConnect's note below). Recovery is an app restart.</summary>
    [ObservableProperty] private bool _isDeviceLost;

    public DeviceRepository? Repository { get; private set; }
    public ReorderService? Reorder { get; private set; }
    public SonuClient? Client { get; private set; }
    public event EventHandler? Connected;

    /// <summary>Raised once when the link dies mid-session, after the VM has entered its dead
    /// state. MainWindowViewModel uses it to stop the background usage scan.</summary>
    public event EventHandler? DeviceLost;

    /// <summary>Connect is only offered while disconnected. Re-opening the transport on an already-live
    /// session resets the ESP32 and builds a second, conflicting link — which wedged the pedal until a
    /// power cycle. Reconnecting after a drop requires restarting the app.</summary>
    private bool CanConnect => !IsConnected && !IsDeviceLost;
    partial void OnIsConnectedChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();
    partial void OnIsDeviceLostChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsConnected) return;   // belt: ExecuteAsync bypasses CanExecute, so guard the reconnect here too
        using var op = _statusService.BeginOperation("Connecting…");
        try
        {
            var state = await _session.ConnectAsync();
            IsConnected = state.Connected;
            if (!state.Connected)
            {
                Status = "Disconnected (no pedal found on USB — check the cable, and close VoidX-Control if it's running)";
                _statusService.Failure("No pedal found on USB");
                return;
            }

            WritesAllowed = state.Compatibility!.WritesAllowed;
            Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility!.Message} ({state.Transport})";
            Client = _session.Client;
            Client!.Disconnected += OnDeviceDisconnected;
            Repository = new DeviceRepository(_session.Client!);
            Reorder = new ReorderService(Repository);
            // Idle summary the bar shows once connect + initial reads finish.
            _statusService.SetIdleSummary($"{state.Device!.Name} {state.Device.Version} ({state.Transport})");
            Connected?.Invoke(this, EventArgs.Empty);

            // Anonymous usage ping: first successful connect per run, at most once per UTC day.
            // Fire-and-forget: ConnectAsync backs an AsyncRelayCommand, which disables the bound
            // Connect button for as long as it runs. Telemetry must never delay or gate the
            // connect flow (the pedal is already connected and usable at this point), so the
            // ping is not awaited even though PingAsync itself never throws and can take up to
            // its HTTP timeout when the network is unreachable.
            if (_usage is not null && !_usagePinged)
            {
                _usagePinged = true;
                _ = _usage.PingAsync(state.Device!.Version, state.Transport);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
            _statusService.Failure($"Connect failed: {ex.Message}");
        }
    }

    /// <summary>Fires on the thread of the failing send, so marshal before touching bound state.
    /// Idempotent: SonuClient raises this once, but the guard keeps a second source harmless.
    ///
    /// <paramref name="ex"/> is deliberately NOT reported to the status bar. It is the BARE
    /// exception the transport threw — SonuClient.Latch receives it straight from the link, while
    /// the slot-naming enrichment happens strictly above SonuClient (SlotBlobService.UploadAsync)
    /// on the instance rethrown to ITS caller, which never reaches this event. So a Failure() here
    /// could only repeat the generic message, and because the interrupted operation's own catch
    /// (e.g. AmpListViewModel's catch (IOException)) runs BEFORE this posted handler, it would
    /// overwrite that catch's enriched, slot-naming message with the less informative one. Status,
    /// the idle summary, and the operation's own catch already cover the reporting.</summary>
    private void OnDeviceDisconnected(DeviceDisconnectedException ex) => _dispatch(() =>
    {
        if (IsDeviceLost) return;
        IsDeviceLost = true;                  // BEFORE IsConnected: CanConnect must already be false
        IsConnected = false;
        Status = "Device disconnected — reconnect the pedal and restart NAMager";
        _statusService.SetIdleSummary("Device disconnected");
        try { _session.Disconnect(); } catch { /* the transport already closed itself */ }
        // A throwing subscriber here would otherwise replace the DeviceDisconnectedException at
        // SonuClient.Latch()'s call site (Latch calls Disconnected?.Invoke, then `throw;` — if the
        // invoke throws, that new exception propagates instead of the original reaching the
        // caller of the failing send). Guard it the same way _session.Disconnect() is guarded
        // above, but log so a broken subscriber is diagnosable rather than silently swallowed.
        try { DeviceLost?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex2) { Log.Warn(ex2, "DeviceLost subscriber threw"); }
    });
}
