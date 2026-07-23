using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;

namespace Namager.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly DeviceSession _session;

    public ConnectionViewModel(DeviceSession session)
    { _session = session; }

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
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
        }
    }
}
