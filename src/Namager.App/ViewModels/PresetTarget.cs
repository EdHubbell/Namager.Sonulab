namespace Namager.App.ViewModels;

/// <summary>The preset the parameter editor should load: its 0-based device slot and its name.
/// The editor selects on the device by NAME (that is what the protocol takes) but needs the INDEX
/// for targeted usage-map maintenance and for downloading the slot's bytes.</summary>
public sealed record PresetTarget(int Index, string Name);
