using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.Core;
using Sonulab.Core.Model;

namespace Sonulab.App.ViewModels;

public partial class ParameterEditorViewModel : ObservableObject
{
    private readonly SonuClient _client;
    public ParameterEditorViewModel(SonuClient client) => _client = client;

    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _presetName = "";

    [RelayCommand]
    private async Task LoadAsync()
    {
        Fields.Clear();
        foreach (var rec in await _client.BrowseRecordsAsync(@"root\app"))
        {
            var schema = NodeSchema.FromRecord(rec);
            if (schema.Type is not ("float" or "enum" or "plist")) continue; // editable leaves only
            var value = rec.Json.TryGetProperty("value", out var v) ? v.GetRawText() : "\"\"";
            Fields.Add(new ParameterFieldViewModel(schema, value));
        }
        IsDirty = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        foreach (var f in Fields)
            await _client.WriteAsync(f.Path, f.ToJsonValue());
        if (!string.IsNullOrEmpty(PresetName))
            await _client.SaveAsync(@"root\app\preset", PresetName);
        IsDirty = false;
    }
}
