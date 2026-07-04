namespace Sonulab.Core.Model;

/// <summary>One of a blob list's 30 slots (root\amp, root\ir). Name "" = empty.</summary>
public sealed record SlotEntry(int Index, string Name)
{
    public bool IsEmpty => string.IsNullOrEmpty(Name);
}
