namespace Sonulab.Core.Model;

public sealed record PresetSlot(int Index, string Name)
{
    public bool IsEmpty => string.IsNullOrEmpty(Name);
}
