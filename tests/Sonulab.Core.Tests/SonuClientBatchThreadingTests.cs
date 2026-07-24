using System.Collections.Concurrent;
using Sonulab.Core;
using Sonulab.Core.Transport;
using Xunit;

/// <summary>Regression cover for the UI-freeze defect found in review (2026-07-24).
///
/// The pipelined batch loop paces itself to the millisecond and, on a host without a raised timer
/// resolution, spins rather than yields. Run on the caller's thread — Avalonia's dispatcher in the
/// app — a 96-chunk read pinned a core and froze the window for its whole duration (measured 106%
/// of one core, zero context posts). SendBatchGatedAsync hops to the pool to prevent that.
///
/// Nothing else catches a regression here: every other batch test runs without a
/// SynchronizationContext, where "the caller's thread" is already a pool thread.</summary>
public class SonuClientBatchThreadingTests
{
    /// <summary>Records which thread the link was actually driven on.</summary>
    private sealed class ThreadRecordingLink : ISonuLink
    {
        public bool RanOnPoolThread;
        public int? RanOnThreadId;
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Close() { }
        public Task<string> SendAsync(string command, CancellationToken ct = default) =>
            Task.FromResult(Window(command));

        public Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<string> commands, CancellationToken ct = default)
        {
            RanOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            RanOnThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult<IReadOnlyList<string>>(commands.Select(Window).ToList());
        }

        private static string Window(string command)
        {
            var m = System.Text.RegularExpressions.Regex.Match(command, @"""index"":(\d+),""chunk"":(-?\d+)");
            return m.Success
                ? $"root\\presets:{{\"index\":{m.Groups[1].Value},\"chunk\":{m.Groups[2].Value},\"value\":\"{new string('0', 256)}\"}}\r\n"
                : "";
        }
    }

    /// <summary>Stands in for Avalonia's dispatcher: a context that runs its work on ONE dedicated
    /// thread. If the batch ran on the caller's context, it would land on that thread.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void Dispose() => _queue.CompleteAdding();

        /// <summary>Runs <paramref name="work"/> ON this context's thread and waits for it.</summary>
        public void Run(Func<Task> work)
        {
            var done = new TaskCompletionSource();
            Post(async _ =>
            {
                try { await work(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            done.Task.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Multi_chunk_read_does_not_run_the_batch_on_the_callers_context()
    {
        var link = new ThreadRecordingLink();
        var client = new SonuClient(link, readRetryAttempts: 1, readRetryDelayMs: 0);
        using var ctx = new SingleThreadContext();

        ctx.Run(() => client.DReadChunkRangeAsync(@"root\presets", 0, 1, 8));

        Assert.True(link.RanOnPoolThread,
            "the pipelined batch ran on the caller's thread — in the app that is the UI thread, " +
            "and its pacing loop would freeze the window for the whole burst");
        Assert.NotEqual(ctx.ThreadId, link.RanOnThreadId);
    }
}
