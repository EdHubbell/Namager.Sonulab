namespace Namager.App.Services;

/// <summary>Navigation seam so a detail view-model can send the user to a preset without depending
/// on MainWindowViewModel. Implemented by MainWindowViewModel; faked in tests.</summary>
public interface IPresetNavigator
{
    /// <summary>Switch to the Presets tab and select <paramref name="index"/>. This ACTIVATES the
    /// preset on the pedal, exactly as clicking it in the preset list does.</summary>
    void NavigateToPreset(int index, string name);
}

/// <summary>Default for hosts that have nowhere to navigate (tests, the flyout before wiring).</summary>
public sealed class NullPresetNavigator : IPresetNavigator
{
    public static readonly NullPresetNavigator Instance = new();
    public void NavigateToPreset(int index, string name) { }
}
