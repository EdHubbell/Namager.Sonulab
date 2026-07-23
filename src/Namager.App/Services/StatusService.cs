using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Namager.App.Services;

public enum StatusKind { Idle, Busy, Success, Error }

public interface IOperationScope : System.IDisposable
{
    void Report(double progress);
    void Report(string message);
}

public interface IStatusService
{
    IOperationScope BeginOperation(string message, bool determinate = false);
    void Success(string message);
    void Failure(string message);
    void SetIdleSummary(string summary);
    void Dismiss();
}

/// <summary>The app's single busy/progress/success/error channel. UI-facing (no protocol
/// logic). Consumed on the UI thread — callers that report from a worker thread (amp upload
/// progress) already marshal via their own dispatcher seam, so this type does not re-marshal.</summary>
public sealed partial class StatusService : ObservableObject, IStatusService
{
    public static readonly System.TimeSpan SuccessDuration = System.TimeSpan.FromSeconds(4);

    // Injected so tests drive the auto-revert without real time passing.
    private readonly System.Func<System.TimeSpan, System.Threading.CancellationToken, System.Threading.Tasks.Task> _delay;

    public StatusService(System.Func<System.TimeSpan, System.Threading.CancellationToken, System.Threading.Tasks.Task>? delay = null)
        => _delay = delay ?? ((ts, ct) => System.Threading.Tasks.Task.Delay(ts, ct));

    private readonly List<Op> _stack = new();
    private string _idleSummary = "Ready";
    private (StatusKind Kind, string Message)? _terminal;
    private System.Threading.CancellationTokenSource? _revertCts;

    [ObservableProperty] private StatusKind _kind = StatusKind.Idle;
    [ObservableProperty] private string _message = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasProgress;

    /// <summary>Test seam: the in-flight success auto-revert (if any).</summary>
    public System.Threading.Tasks.Task? PendingRevert { get; private set; }

    private sealed class Op { public string Message = ""; public bool Determinate; public double Progress; }
    private Op? Top => _stack.Count > 0 ? _stack[^1] : null;

    public IOperationScope BeginOperation(string message, bool determinate = false)
    {
        CancelRevert();
        _terminal = null;
        var op = new Op { Message = message, Determinate = determinate };
        _stack.Add(op);
        Recompute();
        return new Scope(this, op);
    }

    public void Success(string message)
    {
        CancelRevert();
        _terminal = (StatusKind.Success, message);
        Recompute();
        // Only start the countdown once the success message is actually visible
        // (the stack is empty). If an operation is still open, End() schedules
        // the revert once that operation's scope disposes and the stack drains.
        if (_stack.Count == 0) ScheduleRevert();
    }

    public void Failure(string message) { CancelRevert(); _terminal = (StatusKind.Error, message); Recompute(); }
    public void Dismiss() { CancelRevert(); _terminal = null; Recompute(); }
    public void SetIdleSummary(string summary) { _idleSummary = summary; if (_stack.Count == 0 && _terminal is null) Recompute(); }

    private void ReportProgress(Op op, double p) { op.Progress = System.Math.Clamp(p, 0, 1); if (Top == op) Recompute(); }
    private void ReportMessage(Op op, string m) { op.Message = m; if (Top == op) Recompute(); }

    private void End(Op op)
    {
        _stack.Remove(op);
        Recompute();
        // The stack just drained while a success terminal was pending (set via
        // Success() while an op was still open) — the message is visible now,
        // so start its countdown, unless one is already scheduled.
        if (_stack.Count == 0 && _terminal?.Kind == StatusKind.Success && _revertCts is null)
            ScheduleRevert();
    }

    private void Recompute()
    {
        if (Top is { } op)
        {
            Kind = StatusKind.Busy; Message = op.Message;
            IsBusy = true; HasProgress = op.Determinate;
            IsIndeterminate = !op.Determinate; Progress = op.Progress;
        }
        else if (_terminal is { } t)
        {
            Kind = t.Kind; Message = t.Message;
            IsBusy = false; HasProgress = false; IsIndeterminate = false; Progress = 0;
        }
        else
        {
            Kind = StatusKind.Idle; Message = _idleSummary;
            IsBusy = false; HasProgress = false; IsIndeterminate = false; Progress = 0;
        }
    }

    private void ScheduleRevert()
    {
        var cts = new System.Threading.CancellationTokenSource();
        _revertCts = cts;
        PendingRevert = RevertAfterDelay(cts);
    }

    private async System.Threading.Tasks.Task RevertAfterDelay(System.Threading.CancellationTokenSource cts)
    {
        try
        {
            try { await _delay(SuccessDuration, cts.Token); }
            catch (System.OperationCanceledException) { return; }
            if (cts.IsCancellationRequested) return;
            if (_terminal?.Kind == StatusKind.Success)
            {
                _terminal = null;
                if (System.Object.ReferenceEquals(_revertCts, cts)) _revertCts = null;
                Recompute();
            }
        }
        finally
        {
            // Safe even if CancelRevert() already disposed this same instance —
            // CancellationTokenSource.Dispose() is idempotent.
            cts.Dispose();
        }
    }

    private void CancelRevert()
    {
        if (_revertCts is null) return;
        _revertCts.Cancel();
        _revertCts.Dispose();
        _revertCts = null;
    }

    private sealed class Scope(StatusService svc, Op op) : IOperationScope
    {
        private bool _done;
        public void Report(double progress) { if (!_done) svc.ReportProgress(op, progress); }
        public void Report(string message) { if (!_done) svc.ReportMessage(op, message); }
        public void Dispose() { if (_done) return; _done = true; svc.End(op); }
    }
}

/// <summary>No-op fallback so a VM constructed without a status service (existing tests) works.</summary>
public sealed class NullStatusService : IStatusService
{
    public static readonly NullStatusService Instance = new();
    private sealed class NoScope : IOperationScope
    { public void Report(double progress) { } public void Report(string message) { } public void Dispose() { } }
    public IOperationScope BeginOperation(string message, bool determinate = false) => new NoScope();
    public void Success(string message) { }
    public void Failure(string message) { }
    public void SetIdleSummary(string summary) { }
    public void Dismiss() { }
}
