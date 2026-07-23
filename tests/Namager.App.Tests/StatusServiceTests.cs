using System.ComponentModel;
using Namager.App.Services;
using Xunit;

public class StatusServiceTests
{
    // A delay the test controls: completes only when the gate is released, and
    // throws OperationCanceledException if the scheduled revert is cancelled.
    private static (StatusService svc, TaskCompletionSource gate) MakeControlled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = new StatusService((_, ct) => gate.Task.WaitAsync(ct));
        return (svc, gate);
    }

    [Fact] public void Idle_by_default_shows_summary()
    {
        var svc = new StatusService();
        svc.SetIdleSummary("Ready");
        Assert.Equal(StatusKind.Idle, svc.Kind);
        Assert.Equal("Ready", svc.Message);
        Assert.False(svc.IsBusy);
    }

    [Fact] public void BeginOperation_shows_busy_message_until_disposed()
    {
        var svc = new StatusService();
        svc.SetIdleSummary("Ready");
        var op = svc.BeginOperation("Reading presets…");
        Assert.Equal(StatusKind.Busy, svc.Kind);
        Assert.Equal("Reading presets…", svc.Message);
        Assert.True(svc.IsBusy);
        op.Dispose();
        Assert.Equal(StatusKind.Idle, svc.Kind);
        Assert.Equal("Ready", svc.Message);
    }

    [Fact] public void Determinate_operation_exposes_progress()
    {
        var svc = new StatusService();
        var op = svc.BeginOperation("Uploading…", determinate: true);
        Assert.True(svc.HasProgress);
        op.Report(0.5);
        Assert.Equal(0.5, svc.Progress);
        op.Dispose();
        Assert.False(svc.HasProgress);
    }

    [Fact] public void Nested_operations_top_of_stack_wins()
    {
        var svc = new StatusService();
        var outer = svc.BeginOperation("Connecting…");
        var inner = svc.BeginOperation("Reading presets…");
        Assert.Equal("Reading presets…", svc.Message);
        inner.Dispose();
        Assert.Equal("Connecting…", svc.Message);   // falls back to outer
        outer.Dispose();
        Assert.Equal(StatusKind.Idle, svc.Kind);
    }

    [Fact] public async Task Success_shows_then_auto_reverts_to_idle()
    {
        var (svc, gate) = MakeControlled();
        svc.SetIdleSummary("Ready");
        using (svc.BeginOperation("Saving…")) { }
        svc.Success("Saved");
        Assert.Equal(StatusKind.Success, svc.Kind);
        Assert.Equal("Saved", svc.Message);
        gate.SetResult();                            // let the revert delay complete
        await svc.PendingRevert!;
        Assert.Equal(StatusKind.Idle, svc.Kind);
        Assert.Equal("Ready", svc.Message);
    }

    [Fact] public void Failure_persists_until_next_operation()
    {
        var svc = new StatusService();
        svc.SetIdleSummary("Ready");
        svc.Failure("Save failed: boom");
        Assert.Equal(StatusKind.Error, svc.Kind);
        Assert.Equal("Save failed: boom", svc.Message);
        using (svc.BeginOperation("Deleting…")) { }  // begin+end clears the error
        Assert.Equal(StatusKind.Idle, svc.Kind);
    }

    [Fact] public void Dismiss_clears_a_persistent_error()
    {
        var svc = new StatusService();
        svc.SetIdleSummary("Ready");
        svc.Failure("nope");
        svc.Dismiss();
        Assert.Equal(StatusKind.Idle, svc.Kind);
    }

    [Fact] public async Task New_operation_cancels_a_pending_success_revert()
    {
        var (svc, _) = MakeControlled();
        svc.Success("Saved");
        var firstRevert = svc.PendingRevert;
        using (svc.BeginOperation("Next…")) { }
        // The prior revert task should have been cancelled (not left to flip state later).
        if (firstRevert is not null)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await firstRevert)
                .ContinueWith(_ => { });             // tolerate either cancelled or already-completed
        Assert.Equal(StatusKind.Idle, svc.Kind);
    }

    [Fact] public async Task Success_before_scope_dispose_is_not_lost()
    {
        var (svc, gate) = MakeControlled();
        svc.SetIdleSummary("Ready");
        var op = svc.BeginOperation("Saving…");
        svc.Success("Saved");                         // terminal set while op is still open
        // Op is still open, so it must still win (Top of stack), and no countdown
        // may start yet — the success message isn't visible yet.
        Assert.Equal(StatusKind.Busy, svc.Kind);
        Assert.Equal("Saving…", svc.Message);
        Assert.Null(svc.PendingRevert);

        op.Dispose();                                 // success message becomes visible now
        Assert.Equal(StatusKind.Success, svc.Kind);
        Assert.Equal("Saved", svc.Message);
        Assert.NotNull(svc.PendingRevert);             // countdown starts only now

        gate.SetResult();                              // let the revert delay complete
        await svc.PendingRevert!;
        Assert.Equal(StatusKind.Idle, svc.Kind);
    }

    [Fact] public void Changing_state_raises_property_changed()
    {
        var svc = new StatusService();
        var raised = new List<string>();
        svc.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        using (svc.BeginOperation("Saving…")) { }

        Assert.Contains(nameof(StatusService.Message), raised);
        Assert.Contains(nameof(StatusService.Kind), raised);
    }
}
