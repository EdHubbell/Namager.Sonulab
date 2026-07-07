namespace Sonulab.Tone3000;

public enum T3kError { Auth, RateLimited, Network, Api }

/// <summary>Every Tone3000 failure surfaces as this, with a user-honest message
/// (the UI shows Message verbatim) and a Kind the UI can branch on.</summary>
public sealed class T3kException(string message, T3kError kind, Exception? inner = null)
    : Exception(message, inner)
{
    public T3kError Kind { get; } = kind;
}
