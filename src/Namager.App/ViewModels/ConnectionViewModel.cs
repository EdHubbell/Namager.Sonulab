using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;

namespace Namager.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly DeviceSession _session;
    private readonly Namager.App.Services.IUsagePingService? _usage;
    private bool _usagePinged;   // first successful connect of this app run only

    public ConnectionViewModel(DeviceSession session,
                               Namager.App.Services.IUsagePingService? usage = null)
    { _session = session; _usage = usage; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private string _status = "Disconnected";

    public DeviceRepository? Repository { get; private set; }
    public ReorderService? Reorder { get; private set; }
    public SonuClient? Client { get; private set; }
    public event EventHandler? Connected;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            var state = await _session.ConnectAsync();
            IsConnected = state.Connected;
            if (!state.Connected) { Status = "Disconnected (no device found on USB or WiFi)"; return; }

            WritesAllowed = state.Compatibility!.WritesAllowed;
            Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility!.Message} ({state.Transport})";
            Client = _session.Client;
            Repository = new DeviceRepository(_session.Client!);
            Reorder = new ReorderService(Repository);
            Connected?.Invoke(this, EventArgs.Empty);

            // Anonymous usage ping: first successful connect per run, at most once per UTC day.
            // Awaited so tests are deterministic; PingAsync itself never throws and returns
            // immediately when the day is already recorded or this is a dev build.
            if (_usage is not null && !_usagePinged)
            {
                _usagePinged = true;
                await _usage.PingAsync(state.Device!.Version, state.Transport);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
        }
    }
}
