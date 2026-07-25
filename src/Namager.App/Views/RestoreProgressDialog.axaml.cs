using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Namager.App.Services;

namespace Namager.App.Views;

/// <summary>Modal progress for a restore run. Restore takes roughly 10 s per slot, so the user
/// needs both a progress readout and a way out; cancellation lands between slots, never mid-write.</summary>
public partial class RestoreProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private bool _closed;

    public RestoreProgressDialog()
    {
        InitializeComponent();
        // Closing (title-bar X, Alt+F4, ...) is itself a cancel. Latch _closed here too: it runs
        // on the UI thread strictly before the window is torn down, so a same-thread Close() call
        // arriving afterwards (see SafeClose) can never re-enter an already-closing window.
        Closing += (_, _) => { _closed = true; _cts.Cancel(); };
    }

    public static async Task RunAsync(Window owner, RestorePlan plan,
        Func<IProgress<string>, CancellationToken, Task<string>> run)
    {
        var dlg = new RestoreProgressDialog();
        int total = plan.Items.Count;
        int seen = 0;
        var progress = new Progress<string>(msg => Dispatcher.UIThread.Post(() =>
        {
            dlg.ProgressText.Text = msg;
            dlg.Bar.Value = total > 0 ? (double)(++seen) / total : 0;
        }));

        Task<string>? work = null;

        // Race fix: do NOT start `run` before the dialog is shown. ShowDialog's returned Task
        // only completes once Close() runs; if `run` finishes first (an empty plan, or an
        // instantly-dead link) and posts Close() before the window has actually opened, that
        // Close() has nothing to act on, the post is lost, and the still-pending ShowDialog
        // blocks forever with no way for the user to get out. Avalonia raises Opened
        // synchronously, inside the ShowDialog call, once the window is actually on screen — so
        // starting the work from that handler guarantees the window is already open by the time
        // any completion-triggered Close() can run.
        dlg.Opened += (_, _) =>
        {
            work = run(progress, dlg._cts.Token);
            _ = work.ContinueWith(_ => Dispatcher.UIThread.Post(dlg.SafeClose),
                                  TaskScheduler.Default);
        };

        await dlg.ShowDialog(owner);

        if (work is not null)
        {
            try { await work; } catch { /* the VM already reported it on the status bar */ }
        }
    }

    /// <summary>Idempotent: the continuation and a user-initiated Closing can each try to close
    /// the window; only the first one should actually call Close().</summary>
    private void SafeClose()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling — finishing the current preset…";
    }
}
