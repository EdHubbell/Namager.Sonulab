namespace Sonulab.App.Services;

/// <summary>One user feedback submission; becomes a public GitHub issue via the
/// feedback worker (infra/feedback-worker).</summary>
public sealed record FeedbackReport(string Name, string Email, string Message, string AppVersion, string Os);

public interface IFeedbackService
{
    /// <summary>Throws <see cref="FeedbackSendException"/> on any delivery failure.</summary>
    Task SendAsync(FeedbackReport report, CancellationToken ct = default);
}

public sealed class FeedbackSendException(string message, Exception? inner = null)
    : Exception(message, inner);
