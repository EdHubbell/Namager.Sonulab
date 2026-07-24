namespace Sonulab.Core.Transport;

public sealed class SerialLinkOptions
{
    public int PollMs { get; init; } = 10;
    public int IdleGapMs { get; init; } = 120;
    public int MaxWaitMs { get; init; } = 2500;          // ceiling per command; NUL-stop returns earlier for normal responses
    public int FirstByteTimeoutMs { get; init; } = 300;  // if no response byte by now, treat as a no-response command (e.g. a write)
    public int OpenSettleMs { get; init; } = 0;      // delay after Open before first command (ESP32 DTR/RTS reset)
    public int ProbeAttempts { get; init; } = 1;     // identity-probe tries per (port,baud)
    public int ProbeRetryDelayMs { get; init; } = 300;

    /// <summary>Master switch for paced-overlap pipelining in SerialSonuLink.SendBatchAsync.
    /// false → the lockstep fallback, behaviourally identical to N × SendAsync. This is the
    /// kill switch if a cable/hub/firmware combination turns out to drop at the paced rate.</summary>
    public bool PipelineEnabled { get; init; } = true;

    /// <summary>Hard floor between pipelined sends. 30 ms is the probe-proven pace
    /// (PROTOCOL.md "dread limits &amp; hazards"); at 25 ms the firmware drops commands.
    /// Raise it if a device proves flaky — never lower it.</summary>
    public int PipelineMinPaceMs { get; init; } = 30;

    /// <summary>Read-poll interval inside a batch. The lockstep PollMs (10) is too coarse to
    /// land a 30 ms pace cleanly.</summary>
    public int PipelinePollMs { get; init; } = 3;
}
