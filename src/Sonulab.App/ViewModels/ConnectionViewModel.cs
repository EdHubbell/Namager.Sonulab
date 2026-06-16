using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Connection;
using Sonulab.Core.Services;

namespace Sonulab.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly DeviceSession _session;
    private readonly IReadOnlyList<string> _ports;
    private static readonly int[] Bauds = { 115200 };

    public ConnectionViewModel(DeviceSession session, IReadOnlyList<string> ports)
    { _session = session; _ports = ports; }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private string _status = "Disconnected";

    public DeviceRepository? Repository { get; private set; }
    public ReorderService? Reorder { get; private set; }
    public event EventHandler? Connected;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var state = await _session.ConnectAsync(_ports, Bauds);
        IsConnected = state.Connected;
        if (!state.Connected) { Status = "Disconnected (no device found)"; return; }

        WritesAllowed = state.Compatibility!.WritesAllowed;
        Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility.Status}";
        Repository = new DeviceRepository(_session.Client!);
        Reorder = new ReorderService(Repository);
        Connected?.Invoke(this, EventArgs.Empty);
    }
}
