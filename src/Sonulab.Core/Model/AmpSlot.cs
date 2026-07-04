namespace Sonulab.Core.Model;

/// <summary>One of the 30 amp slots (root\amp). Name "" = empty.</summary>
public sealed record AmpSlot(int Index, string Name)
{
    public bool IsEmpty => string.IsNullOrEmpty(Name);
}
