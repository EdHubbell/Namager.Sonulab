# Status & Polish Release — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the app one consistent, always-visible status/progress signal (bottom status bar) for connect, long operations, and write success/failure, plus the layout/interaction polish fixes from issue #5.

**Architecture:** A new `StatusService` (single shared instance, no DI container — created in `MainWindowViewModel` and passed to child VMs through an optional constructor parameter) is the app's one channel for busy/progress/success/error. VMs report through `IStatusService`; a bottom status bar in `MainWindow` binds to the concrete `StatusService`'s observable properties. Existing per-list busy indicators and scattered inline error text are removed from the three list views. Field-anchored errors (parameter editor, amp upload panel) stay inline.

**Tech Stack:** Avalonia 12 (built-in FluentTheme), CommunityToolkit.Mvvm (`[ObservableProperty]`/`[RelayCommand]`), xUnit. Theme tokens in `Styles/SonulabTheme.axaml` (`Sonulab.*Brush`).

## Global Constraints

- **Avalonia 12 + built-in FluentTheme only. Never add FluentAvalonia** (crashes on 12). Icons are built-in `PathIcon` geometries.
- **No hardcoded hex colors in `.axaml`** — use `Sonulab.*` dynamic-resource tokens (both light/dark variants) or converters that resolve them.
- **No DI container** — VMs are constructed manually in `MainWindowViewModel`. Shared services are passed via constructor parameters. New service parameters are **optional (`= null`)** with an internal no-op fallback, so existing tests that construct VMs without the service keep compiling.
- **Device names cap ~31 chars** (unchanged; not touched here).
- **Crash-guard invariant:** a device/transport exception must NEVER escape a `[RelayCommand]` onto the UI thread (it tears down the process). Every command keeps its broad `catch`. Status reporting is added *inside* those existing guards, never around them.
- Build: `dotnet build`. Test: `dotnet test` (all pass; currently 490 tests).

---

### Task 1: `StatusService` (core status/progress service)

**Files:**
- Create: `src/Namager.App/Services/StatusService.cs`
- Test: `tests/Namager.App.Tests/StatusServiceTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `enum StatusKind { Idle, Busy, Success, Error }`
  - `interface IOperationScope : IDisposable { void Report(double progress); void Report(string message); }`
  - `interface IStatusService { IOperationScope BeginOperation(string message, bool determinate = false); void Success(string message); void Failure(string message); void SetIdleSummary(string summary); void Dismiss(); }`
  - `sealed partial class StatusService : ObservableObject, IStatusService` with observable properties `StatusKind Kind`, `string Message`, `double Progress`, `bool IsIndeterminate`, `bool IsBusy`, `bool HasProgress`; a constructor `StatusService(Func<TimeSpan, CancellationToken, Task>? delay = null)`; and `public Task? PendingRevert { get; private set; }`.
  - `sealed class NullStatusService : IStatusService` (no-op singleton `Instance`) for VM fallbacks.

Behavior contract (drives the tests):
- Operations are a stack. While non-empty, `Kind == Busy` and `Message`/`Progress`/`HasProgress` reflect the top operation.
- When the stack empties: if a terminal (`Success`/`Failure`) is pending, show it; otherwise show `Idle` with the idle summary.
- `Success` shows the message, then auto-reverts to Idle after `SuccessDuration` (uses the injected `delay`). `Failure` persists until the next `BeginOperation`, `Dismiss`, or another terminal.
- A new `BeginOperation` or terminal cancels any pending success auto-revert.

- [ ] **Step 1: Write the failing tests**

```csharp
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~StatusServiceTests`
Expected: FAIL — `StatusService`/`IStatusService` do not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
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

    public void Success(string message) { CancelRevert(); _terminal = (StatusKind.Success, message); Recompute(); ScheduleRevert(); }
    public void Failure(string message) { CancelRevert(); _terminal = (StatusKind.Error, message); Recompute(); }
    public void Dismiss() { CancelRevert(); _terminal = null; Recompute(); }
    public void SetIdleSummary(string summary) { _idleSummary = summary; if (_stack.Count == 0 && _terminal is null) Recompute(); }

    private void ReportProgress(Op op, double p) { op.Progress = System.Math.Clamp(p, 0, 1); if (Top == op) Recompute(); }
    private void ReportMessage(Op op, string m) { op.Message = m; if (Top == op) Recompute(); }
    private void End(Op op) { _stack.Remove(op); Recompute(); }

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
        try { await _delay(SuccessDuration, cts.Token); }
        catch (System.OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        if (_terminal?.Kind == StatusKind.Success) { _terminal = null; Recompute(); }
    }

    private void CancelRevert() { _revertCts?.Cancel(); _revertCts = null; }

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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~StatusServiceTests`
Expected: PASS (all 9).

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Services/StatusService.cs tests/Namager.App.Tests/StatusServiceTests.cs
git commit -m "feat(status): StatusService — single busy/progress/success/error channel"
```

---

### Task 2: `StatusKindToBrush` converter

**Files:**
- Modify: `src/Namager.App/Converters/Converters.cs` (append a new converter class)
- Test: `tests/Namager.App.Tests/StatusKindToBrushTests.cs`

**Interfaces:**
- Consumes: `StatusKind` (Task 1).
- Produces: `sealed class StatusKindToBrush : IValueConverter` with `static readonly StatusKindToBrush Instance`. Maps `Error → Sonulab.DangerBrush`, `Success → Sonulab.SuccessBrush`, everything else → `Sonulab.TextBrush` (fallbacks used when the theme layer isn't loaded, mirroring `BoolToBrush`).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Globalization;
using Avalonia.Media;
using Namager.App.Converters;
using Namager.App.Services;
using Xunit;

public class StatusKindToBrushTests
{
    [Theory]
    [InlineData(StatusKind.Error)]
    [InlineData(StatusKind.Success)]
    [InlineData(StatusKind.Busy)]
    [InlineData(StatusKind.Idle)]
    public void Returns_a_brush_for_every_kind(StatusKind kind)
    {
        var result = StatusKindToBrush.Instance.Convert(kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.IsAssignableFrom<IBrush>(result);
    }

    [Fact]
    public void Error_and_success_map_to_different_brushes()
    {
        var err = StatusKindToBrush.Instance.Convert(StatusKind.Error, typeof(IBrush), null, CultureInfo.InvariantCulture);
        var ok  = StatusKindToBrush.Instance.Convert(StatusKind.Success, typeof(IBrush), null, CultureInfo.InvariantCulture);
        Assert.NotEqual(err, ok);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~StatusKindToBrushTests`
Expected: FAIL — `StatusKindToBrush` does not exist.

- [ ] **Step 3: Append the converter to `Converters.cs`**

Add at the end of `src/Namager.App/Converters/Converters.cs` (uses the existing `BoolToBrush.ResolveBrush` helper and `Namager.App.Services.StatusKind`):

```csharp
/// <summary>StatusKind -> brush for the status-bar text. Error => Danger, Success => Success,
/// else default text. Resolves theme tokens at convert time (falls back to fixed brushes when
/// the theme layer isn't loaded, e.g. bare converter tests).</summary>
public sealed class StatusKindToBrush : IValueConverter
{
    public static readonly StatusKindToBrush Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value switch
        {
            Namager.App.Services.StatusKind.Error   => BoolToBrush.ResolveBrush("Sonulab.DangerBrush", Brushes.OrangeRed),
            Namager.App.Services.StatusKind.Success => BoolToBrush.ResolveBrush("Sonulab.SuccessBrush", Brushes.LimeGreen),
            _                                       => BoolToBrush.ResolveBrush("Sonulab.TextBrush", Brushes.Gray),
        };
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}
```

Note: verify `Sonulab.TextBrush` exists in `Styles/SonulabTheme.axaml`; if the primary text token has a different name (e.g. `Sonulab.ForegroundBrush`), use that exact key instead.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~StatusKindToBrushTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/Converters/Converters.cs tests/Namager.App.Tests/StatusKindToBrushTests.cs
git commit -m "feat(status): StatusKind -> brush converter for the status bar"
```

---

### Task 3: Bottom status bar UI + `MainWindowViewModel` wiring + connect hourglass

**Files:**
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs` (add the shared `StatusService`, expose it, set an initial idle summary)
- Modify: `src/Namager.App/Views/MainWindow.axaml` (add the docked bottom bar; connect-button busy label)

**Interfaces:**
- Consumes: `StatusService`, `IStatusService` (Task 1), `StatusKindToBrush` (Task 2).
- Produces: `public StatusService Status { get; }` on `MainWindowViewModel` — later tasks pass it into child VMs.

This task is UI + wiring; it is verified by `dotnet build` and the manual checklist (Task 10), not unit tests.

- [ ] **Step 1: Add `Status` to `MainWindowViewModel`**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, add a field near the other `[ObservableProperty]` VMs (top of the class body):

```csharp
    /// <summary>The single status/progress channel, bound by the bottom status bar and shared
    /// with every child VM (passed via their constructors). Created once, lives for the app.</summary>
    public StatusService Status { get; } = new();
```

At the end of the `MainWindowViewModel()` constructor (after the existing body), seed the idle summary:

```csharp
        Status.SetIdleSummary("Not connected");
```

(`StatusService` is in `Namager.App.Services`, already imported via `using Namager.App.Services;` at the top of the file.)

- [ ] **Step 2: Add the status bar to `MainWindow.axaml`**

In `src/Namager.App/Views/MainWindow.axaml`, insert this **between** the update-available banner `</Border>` (line ~50) and the `<!-- ===== SplitView ===== -->` block. A `DockPanel.Dock="Bottom"` child must be declared before the filling `SplitView`:

```xml
    <!-- ===== Bottom status bar ===== -->
    <Border DockPanel.Dock="Bottom" Padding="12,4"
            Background="{DynamicResource Sonulab.SurfaceAltBrush}"
            BorderBrush="{DynamicResource Sonulab.BorderBrush}" BorderThickness="0,1,0,0">
      <Grid ColumnDefinitions="*,Auto">
        <TextBlock Grid.Column="0" VerticalAlignment="Center" FontSize="12" TextTrimming="CharacterEllipsis"
                   Text="{Binding Status.Message}"
                   Foreground="{Binding Status.Kind, Converter={x:Static conv:StatusKindToBrush.Instance}}"/>
        <ProgressBar Grid.Column="1" Width="160" Height="6" VerticalAlignment="Center" Margin="12,0,0,0"
                     Minimum="0" Maximum="1"
                     Value="{Binding Status.Progress}"
                     IsIndeterminate="{Binding Status.IsIndeterminate}"
                     IsVisible="{Binding Status.IsBusy}"/>
      </Grid>
    </Border>
```

(`conv` is already declared: `xmlns:conv="using:Namager.App.Converters"`.)

- [ ] **Step 3: Add the connect-button busy state (hourglass equivalent)**

In `src/Namager.App/Views/MainWindow.axaml`, replace the Connect button's inner `StackPanel` (lines ~25-28) so the label switches to "Connecting…" while the command runs (the `AsyncRelayCommand` already disables the button):

```xml
          <StackPanel Orientation="Horizontal">
            <PathIcon Data="{StaticResource Icon.Power}" Width="16" Height="16"
                      IsVisible="{Binding !Connection.ConnectCommand.IsRunning}"/>
            <ProgressBar Width="16" Height="16" IsIndeterminate="True"
                         IsVisible="{Binding Connection.ConnectCommand.IsRunning}"/>
            <TextBlock Margin="6,0,0,0" VerticalAlignment="Center"
                       Text="{Binding Connection.ConnectCommand.IsRunning, Converter={x:Static conv:ConnectLabel.Instance}}"/>
          </StackPanel>
```

Add a tiny label converter at the end of `src/Namager.App/Converters/Converters.cs`:

```csharp
/// <summary>bool (command running) -> connect button label.</summary>
public sealed class ConnectLabel : IValueConverter
{
    public static readonly ConnectLabel Instance = new();
    public object? Convert(object? value, Type _, object? __, CultureInfo ___) =>
        value is true ? "Connecting…" : "Connect";
    public object? ConvertBack(object? v, Type _, object? __, CultureInfo ___) => throw new NotSupportedException();
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: build succeeds. (The bar shows "Not connected" at rest; nothing reports to it yet — that arrives in Tasks 4-8.)

- [ ] **Step 5: Commit**

```bash
git add src/Namager.App/ViewModels/MainWindowViewModel.cs src/Namager.App/Views/MainWindow.axaml src/Namager.App/Converters/Converters.cs
git commit -m "feat(status): bottom status bar + connect busy label; wire StatusService into MainWindowViewModel"
```

---

### Task 4: `ConnectionViewModel` — staged connect status

**Files:**
- Modify: `src/Namager.App/ViewModels/ConnectionViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:100` (pass `Status` to the `ConnectionViewModel` ctor)
- Test: `tests/Namager.App.Tests/ConnectionViewModelTests.cs` (add cases + a shared fake)

**Interfaces:**
- Consumes: `IStatusService`, `NullStatusService` (Task 1).
- Produces: `ConnectionViewModel(DeviceSession session, IUsagePingService? usage = null, IStatusService? status = null)`.

- [ ] **Step 1: Add the shared test fake and failing tests**

Create `tests/Namager.App.Tests/FakeStatusService.cs` (reused by Tasks 4-8):

```csharp
using System.Collections.Generic;
using Namager.App.Services;

/// <summary>Records every call so VM tests can assert what was reported to the status channel.</summary>
public sealed class FakeStatusService : IStatusService
{
    public List<string> Begun { get; } = new();
    public List<string> Succeeded { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> IdleSummaries { get; } = new();

    public IOperationScope BeginOperation(string message, bool determinate = false)
    { Begun.Add(message); return new Scope(); }
    public void Success(string message) => Succeeded.Add(message);
    public void Failure(string message) => Failed.Add(message);
    public void SetIdleSummary(string summary) => IdleSummaries.Add(summary);
    public void Dismiss() { }

    private sealed class Scope : IOperationScope
    { public void Report(double progress) { } public void Report(string message) { } public void Dispose() { } }
}
```

Add these tests to `tests/Namager.App.Tests/ConnectionViewModelTests.cs`:

```csharp
    [Fact] public async Task Connect_reports_connecting_and_sets_idle_summary()
    {
        var status = new FakeStatusService();
        var vm = new ConnectionViewModel(Session(), status: status);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Contains("Connecting…", status.Begun);
        Assert.Contains(status.IdleSummaries, s => s.Contains("AMP Station"));
        Assert.Empty(status.Failed);
    }

    [Fact] public async Task Failed_connect_reports_failure_to_status()
    {
        var status = new FakeStatusService();
        var session = new DeviceSession(
            new ILinkProvider[] { new FixedProvider("USB", null), new FixedProvider("WiFi", null) },
            new CompatibilityChecker(FirmwareCatalog.Default));
        var vm = new ConnectionViewModel(session, status: status);

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Contains("Connecting…", status.Begun);
        Assert.Single(status.Failed);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ConnectionViewModelTests`
Expected: FAIL — `ConnectionViewModel` has no `status` parameter (compile error).

- [ ] **Step 3: Implement in `ConnectionViewModel`**

Change the fields + constructor:

```csharp
    private readonly DeviceSession _session;
    private readonly Namager.App.Services.IUsagePingService? _usage;
    private readonly Namager.App.Services.IStatusService _status;
    private bool _usagePinged;

    public ConnectionViewModel(DeviceSession session,
                               Namager.App.Services.IUsagePingService? usage = null,
                               Namager.App.Services.IStatusService? status = null)
    { _session = session; _usage = usage; _status = status ?? Namager.App.Services.NullStatusService.Instance; }
```

Rewrite `ConnectAsync` to wrap the connect in an operation and report the terminal state (keep the existing catch and the fire-and-forget usage ping unchanged):

```csharp
    [RelayCommand]
    private async Task ConnectAsync()
    {
        using var op = _status.BeginOperation("Connecting…");
        try
        {
            var state = await _session.ConnectAsync();
            IsConnected = state.Connected;
            if (!state.Connected)
            {
                Status = "Disconnected (no device found on USB or WiFi)";
                _status.Failure("No device found on USB or WiFi");
                return;
            }

            WritesAllowed = state.Compatibility!.WritesAllowed;
            Status = $"{state.Device!.Name} {state.Device.Version} — {state.Compatibility!.Message} ({state.Transport})";
            Client = _session.Client;
            Repository = new DeviceRepository(_session.Client!);
            Reorder = new ReorderService(Repository);
            // Idle summary the bar shows once connect + initial reads finish.
            _status.SetIdleSummary($"{state.Device!.Name} {state.Device.Version} ({state.Transport})");
            Connected?.Invoke(this, EventArgs.Empty);

            if (_usage is not null && !_usagePinged)
            {
                _usagePinged = true;
                _ = _usage.PingAsync(state.Device!.Version, state.Transport);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"Error: {ex.Message}";
            _status.Failure($"Connect failed: {ex.Message}");
        }
    }
```

Add `using Sonulab.Core.Services;` is already present. Note the `Connected` event fires while `op` is still on the stack, so the child list reads that follow nest above the idle summary — their "Reading presets…" messages (Tasks 5/7/8) show on top, then the bar settles to the idle summary.

- [ ] **Step 4: Pass `Status` at the construction site**

In `src/Namager.App/ViewModels/MainWindowViewModel.cs`, change line ~100:

```csharp
        _connection = new ConnectionViewModel(session, new UsagePingService(), Status);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ConnectionViewModelTests`
Expected: PASS (existing + 2 new). Then `dotnet build` to confirm the call-site change compiles.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ConnectionViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/FakeStatusService.cs tests/Namager.App.Tests/ConnectionViewModelTests.cs
git commit -m "feat(status): ConnectionViewModel reports connecting/idle-summary/failure"
```

---

### Task 5: `PresetListViewModel` — report ops to the status bar

**Files:**
- Modify: `src/Namager.App/ViewModels/PresetListViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:105-108` (pass `Status`)
- Test: `tests/Namager.App.Tests/PresetListViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `IStatusService`, `NullStatusService` (Task 1), `FakeStatusService` (Task 4).
- Produces: `PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed, IStatusService? status = null)`. `IsBusy`/`ErrorMessage` are retained (they still gate `IsEnabled` and are asserted by existing tests); only their *display* moves to the bar. `RunAsync` gains a `success` message parameter.

- [ ] **Step 1: Add failing tests**

```csharp
    [Fact] public async Task Delete_reports_success_to_status()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        var status = new FakeStatusService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, status: status);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Contains(status.Succeeded, m => m.Contains("Deleted") && m.Contains("A"));
        Assert.Empty(status.Failed);
    }

    [Fact] public async Task Refresh_reports_reading_presets()
    {
        var dev = new FakePresetDevice();
        dev.SeedSlot(0, "A", new[] { @"root\app\amp\amp:{""value"":""mA""}" });
        dev.OpenAsync().GetAwaiter().GetResult();
        var repo = new DeviceRepository(new SonuClient(dev));
        var status = new FakeStatusService();
        var vm = new PresetListViewModel(repo, new ReorderService(repo), writesAllowed: true, status: status);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("Reading presets…", status.Begun);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~PresetListViewModelTests`
Expected: FAIL — no `status` parameter (compile error).

- [ ] **Step 3: Implement**

Change fields + constructor:

```csharp
    private readonly DeviceRepository _repo;
    private readonly ReorderService _reorder;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;

    public PresetListViewModel(DeviceRepository repo, ReorderService reorder, bool writesAllowed,
                               Namager.App.Services.IStatusService? status = null)
    { _repo = repo; _reorder = reorder; _writes = writesAllowed; _status = status ?? Namager.App.Services.NullStatusService.Instance; }
```

Rewrite `RunAsync` to take a `success` message and drive the status service (keep `IsBusy`/`ErrorMessage`/`BusyMessage` and the crash-guard exactly as they are):

```csharp
    private async Task<bool> RunAsync(string message, string success, Func<Task> work)
    {
        if (!_writes) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try
        {
            await work();
            await ReloadAsync();
            _status.Success(success);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "preset operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            try { await ReloadAsync(); }
            catch (Exception reloadEx) { Log.Warn(reloadEx, "reload after a failed operation also failed"); }
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update `RefreshAsync` to report a "Reading presets…" op (keep the crash-guard):

```csharp
    [RelayCommand] private async Task RefreshAsync()
    {
        IsBusy = true; BusyMessage = "Reading presets…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading presets…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "preset refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update every `RunAsync` caller to pass a success message (new second argument). The five reorder callers:

```csharp
    // MoveUpAsync
    if (await RunAsync($"Moving slot {s.DisplaySlot} up…", $"Moved '{s.Name}' up", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
    // MoveDownAsync
    if (await RunAsync($"Moving slot {s.DisplaySlot} down…", $"Moved '{s.Name}' down", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
    // MoveItemUpAsync
    if (await RunAsync($"Moving '{s.Name}' up…", $"Moved '{s.Name}' up", () => _reorder.MoveStepAsync(s.Index, up: true)) && dest < Items.Count)
    // MoveItemDownAsync
    if (await RunAsync($"Moving '{s.Name}' down…", $"Moved '{s.Name}' down", () => _reorder.MoveStepAsync(s.Index, up: false)) && dest < Items.Count)
```

And the full `DuplicateAsync`, `DeleteAsync`, `CommitRenameAsync` bodies:

```csharp
    [RelayCommand] private async Task DuplicateAsync()
    {
        if (Selected is not { IsEmpty: false } s) return;
        int dest = Items.FirstOrDefault(i => i.IsEmpty)?.Index ?? -1;
        if (dest < 0) return;
        await RunAsync($"Duplicating '{s.Name}'…", $"Duplicated '{s.Name}'", () => _repo.DuplicateAsync(s.Index, dest, s.Name + " copy"));
    }

    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s) await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _repo.DeleteAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(PresetItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _repo.RenameAsync(s.Index, name)))
            s.IsEditing = false;
    }
```

- [ ] **Step 4: Pass `Status` at the construction site**

In `MainWindowViewModel.cs` (~line 105), change the `PresetListViewModel` construction:

```csharp
            var presets = new PresetListViewModel(
                _connection.Repository!,
                _connection.Reorder!,
                _connection.WritesAllowed,
                Status);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~PresetListViewModelTests`
Expected: PASS (existing + 2 new). Then `dotnet build`.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/PresetListViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/PresetListViewModelTests.cs
git commit -m "feat(status): PresetListViewModel reports operations to the status bar"
```

---

### Task 6: `ParameterEditorViewModel` — explicit "✓ Saved" / failure

**Files:**
- Modify: `src/Namager.App/ViewModels/ParameterEditorViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:109` (pass `Status`)
- Test: `tests/Namager.App.Tests/ParameterEditorViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `IStatusService`, `NullStatusService` (Task 1), `FakeStatusService` (Task 4).
- Produces: `ParameterEditorViewModel(SonuClient client, LabelService? labels = null, ParameterExposure? exposure = null, IStatusService? status = null)`.

- [ ] **Step 1: Add a failing test**

The existing file has a `Dev()` fake-link factory and a `Vm(FakeSonuLink d)` helper. Add a status-aware test that constructs the VM directly with a `FakeStatusService` (4th ctor arg), loads, sets a preset name so `SaveAsync` runs the device save, and asserts the terminal:

```csharp
    [Fact] public async Task Save_reports_saved_to_status()
    {
        var d = Dev(); await d.OpenAsync();
        var status = new FakeStatusService();
        var vm = new ParameterEditorViewModel(new SonuClient(d),
            new LabelService(new Dictionary<string, string>()),
            new ParameterExposure(new[] { @"root\app\amp\sag" }),
            status);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.PresetName = "P1";                     // makes SaveAsync issue the device save
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Saved", status.Succeeded);
        Assert.Empty(status.Failed);
    }
```

If `FakeSonuLink` rejects the bare save write (making this report a failure instead), seed the save the same way the existing preset-save tests do — but `FakeSonuLink` accepts generic writes, so the happy path above should hold.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ParameterEditorViewModelTests`
Expected: FAIL — no `status` parameter.

- [ ] **Step 3: Implement**

Change fields + constructor:

```csharp
    private readonly SonuClient _client;
    private readonly LabelService _labels;
    private readonly ParameterExposure _exposure;
    private readonly Namager.App.Services.IStatusService _status;

    public ParameterEditorViewModel(SonuClient client, LabelService? labels = null,
                                    ParameterExposure? exposure = null,
                                    Namager.App.Services.IStatusService? status = null)
    {
        _client = client;
        _labels = labels ?? LabelService.Default;
        _exposure = exposure ?? ParameterExposure.Default;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
    }
```

Rewrite `SaveAsync` (keep the inline `ErrorMessage` — it's field-anchored next to the Save button — and add the status terminal):

```csharp
    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        using var op = _status.BeginOperation("Saving preset…");
        try
        {
            foreach (var f in AllFields().Where(f => f.IsDirty))
                await _client.WriteAsync(f.Path, f.ToJsonValue());
            if (!string.IsNullOrEmpty(PresetName))
                await _client.SaveAsync(@"root\app\preset", PresetName);
            foreach (var f in AllFields()) f.MarkClean();
            IsDirty = false;
            _status.Success("Saved");
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "parameter save failed");
            ErrorMessage = $"Save failed: {ex.Message}";
            _status.Failure($"Save failed: {ex.Message}");
        }
    }
```

- [ ] **Step 4: Pass `Status` at the construction site**

In `MainWindowViewModel.cs` (~line 109):

```csharp
            var editor = new ParameterEditorViewModel(_connection.Client!, status: Status);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~ParameterEditorViewModelTests`
Expected: PASS. Then `dotnet build`.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/ParameterEditorViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/ParameterEditorViewModelTests.cs
git commit -m "feat(status): explicit Saved/Save-failed feedback on preset save"
```

---

### Task 7: `AmpListViewModel` — status reporting + upload auto-close

**Files:**
- Modify: `src/Namager.App/ViewModels/AmpListViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:120-122` (pass `Status`)
- Test: `tests/Namager.App.Tests/AmpListViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `IStatusService`, `NullStatusService` (Task 1), `FakeStatusService` (Task 4).
- Produces: `AmpListViewModel(AmpService amps, bool writesAllowed, IStatusService? status = null, DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null)`. **`status` is inserted as the 3rd parameter, before the existing optionals** — check existing `AmpListViewModelTests` for any call that passes `distill`/`distilledDir`/`dispatch` positionally and update those to named arguments.

- [ ] **Step 1: Add failing tests**

The file has a private `Make(bool writes = true)` returning `(AmpListViewModel vm, FakeAmpDevice dev)` and helpers `RealisticBlob(byte fill)` + `_backupDir`. Add a grounded delete test that constructs the VM with a `FakeStatusService` (mirroring `Make` inline so it can capture `status`):

```csharp
    [Fact] public async Task Delete_reports_success_to_status()
    {
        var dev = new FakeAmpDevice();
        dev.SeedAmp(0, "Clean", RealisticBlob(1));
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new AmpService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var status = new FakeStatusService();
        var vm = new AmpListViewModel(svc, writesAllowed: true, status: status);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Contains(status.Succeeded, m => m.Contains("Deleted"));
    }
```

For the upload auto-close behavior, find the existing successful-`StartUploadCommand` test in this file (it already sets up a `.vxamp` on disk and the distill/upload fakes). Copy it, add `status: new FakeStatusService()` to the ctor, and assert BOTH new facts after the upload succeeds:

```csharp
        Assert.False(vm.IsUploadPanelOpen);                        // #5: auto-closed on success
        Assert.Contains(status.Succeeded, m => m.Contains("Uploaded"));
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~AmpListViewModelTests`
Expected: FAIL — no `status` parameter.

- [ ] **Step 3: Implement**

Change fields + constructor:

```csharp
    private readonly AmpService _amps;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;
    // ... existing distill/dispatch fields ...

    public AmpListViewModel(AmpService amps, bool writesAllowed,
        Namager.App.Services.IStatusService? status = null,
        DistillRunner? distill = null, string? distilledDir = null, Action<Action>? dispatch = null)
    {
        _amps = amps; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _distill = distill ?? Sonulab.Distill.Distiller.DistillAsync;
        _distilledDir = distilledDir ?? Path.Combine("NAMFiles", "Distilled");
        _dispatch = dispatch ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
    }
```

Rewrite `RunAsync` to take a `success` message (keep the details-drain and crash-guard exactly):

```csharp
    private async Task<bool> RunAsync(string message, string success, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        _detailsCts?.Cancel();
        if (DetailsLoadTask is { } detailsLoad)
        { try { await detailsLoad; } catch { } }
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try { await work(); await ReloadAsync(); _status.Success(success); return true; }
        catch (AmpServiceException ex) { ErrorMessage = ex.Message; _status.Failure(ex.Message); return false; }
        catch (Exception ex)
        {
            Log.Warn(ex, "amp operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update `RefreshAsync` to report "Reading amps…":

```csharp
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsBusy = true; BusyMessage = "Reading amps…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading amps…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "amp refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update the two `RunAsync` callers to pass a success message:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _amps.DeleteAmpAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(AmpItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _amps.RenameAmpAsync(s.Index, name)))
            s.IsEditing = false;
    }
```

For `SaveMetadataAsync`, its `RunAsync` call also needs the new `success` argument:

```csharp
        if (await RunAsync($"Saving metadata for '{name}'…", $"Saved metadata for '{name}'", async () =>
            { /* ...existing body unchanged... */ }))
```

In `StartUploadAsync`, wrap the upload in a status operation and auto-close the panel on success. Add the operation scope right after the duplicate-name validation (before `IsUploading = true;`):

```csharp
        UploadError = null;
        IsUploading = true;
        _uploadCts = new CancellationTokenSource();
        using var op = _status.BeginOperation($"Uploading '{name}'…");
        try
        {
            // ...existing distill + device-write body unchanged, up to and including:
            UploadStatus = $"Done — '{name}' in slot {slot + 1}";
            await ReloadAsync();
            Selected = Items.FirstOrDefault(i => i.Index == slot);
            DetailsLoadTask = LoadDetailsCoreAsync(Selected);
            await DetailsLoadTask;

            IsUploadPanelOpen = false;                               // #5: auto-close into the detail view
            _status.Success($"Uploaded '{name}' to slot {slot + 1}");
        }
        catch (OperationCanceledException) { UploadError = "Cancelled."; }
        catch (Sonulab.Distill.DistillException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (AmpServiceException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (IOException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (UnauthorizedAccessException ex) { UploadError = ex.Message; _status.Failure(ex.Message); }
        catch (Exception ex)
        {
            Log.Warn(ex, "amp upload failed");
            UploadError = $"Upload failed: {ex.Message}";
            _status.Failure($"Upload failed: {ex.Message}");
        }
        finally
        {
            IsUploading = false; CanCancelUpload = false; IsUploadIndeterminate = false;
            _uploadCts?.Dispose(); _uploadCts = null;
        }
```

(Do NOT auto-close on the error/cancel paths — the panel stays open so `UploadError` is visible. A cancel is not reported as a status failure.)

- [ ] **Step 4: Pass `Status` at the construction site**

In `MainWindowViewModel.cs` (~line 120-122):

```csharp
            var ampService = new AmpService(
                _connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var amps = new AmpListViewModel(ampService, _connection.WritesAllowed, Status);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~AmpListViewModelTests`
Expected: PASS (existing + new). Then `dotnet build`.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/AmpListViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/AmpListViewModelTests.cs
git commit -m "feat(status): AmpListViewModel status reporting + upload auto-close (#5)"
```

---

### Task 8: `IrListViewModel` — status reporting

**Files:**
- Modify: `src/Namager.App/ViewModels/IrListViewModel.cs`
- Modify: `src/Namager.App/ViewModels/MainWindowViewModel.cs:125-126` (pass `Status`)
- Test: `tests/Namager.App.Tests/IrListViewModelTests.cs` (add cases)

**Interfaces:**
- Consumes: `IStatusService`, `NullStatusService` (Task 1), `FakeStatusService` (Task 4).
- Produces: `IrListViewModel(IrService irs, bool writesAllowed, IStatusService? status = null, Func<string, byte[]>? convertWav = null)`. **`status` is inserted before `convertWav`** — update any positional `convertWav` call in existing tests to a named argument.

- [ ] **Step 1: Add a failing test**

The file has a private `Make(bool writes = true, int seed = 2)` returning `(IrListViewModel vm, FakeIrDevice dev, List<string> converted)` and a `_backupDir`. Add a grounded delete test constructing the VM with a `FakeStatusService`:

```csharp
    [Fact] public async Task Delete_reports_success_to_status()
    {
        var dev = new FakeIrDevice();
        dev.SeedIr(0, "Ir0", Enumerable.Repeat((byte)1, 4096).ToArray());
        dev.OpenAsync().GetAwaiter().GetResult();
        var svc = new IrService(new SonuClient(dev), _backupDir, paceMs: 0, settleMs: 0);
        var status = new FakeStatusService();
        var vm = new IrListViewModel(svc, writesAllowed: true, status: status);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.Selected = vm.Items[0];
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Contains(status.Succeeded, m => m.Contains("Deleted"));
    }
```

For the upload path, find the existing successful `.wav`/`.irblob` `StartUploadCommand` test, copy it, add `status: new FakeStatusService()` to the ctor, and assert `status.Succeeded` contains "Uploaded" after the upload completes. (IR keeps its panel open on done — do NOT assert `IsUploadPanelOpen == false`; that auto-close is amp-only per #5.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~IrListViewModelTests`
Expected: FAIL — no `status` parameter.

- [ ] **Step 3: Implement**

Change fields + constructor:

```csharp
    private readonly IrService _irs;
    private readonly bool _writes;
    private readonly Namager.App.Services.IStatusService _status;
    private readonly Func<string, byte[]> _convertWav;

    public IrListViewModel(IrService irs, bool writesAllowed,
                           Namager.App.Services.IStatusService? status = null,
                           Func<string, byte[]>? convertWav = null)
    {
        _irs = irs; _writes = writesAllowed;
        _status = status ?? Namager.App.Services.NullStatusService.Instance;
        _convertWav = convertWav ?? Sonulab.Distill.WavToIr.Convert;
    }
```

**Fix the existing `Make` factory first:** it constructs `new IrListViewModel(svc, writes, p => {...})`, passing `convertWav` positionally. Inserting `status` as the 3rd parameter breaks this. Change that line to name the argument:

```csharp
        var vm = new IrListViewModel(svc, writes, convertWav: p => { converted.Add(p); return Enumerable.Repeat((byte)0xC0, 4096).ToArray(); });
```

Rewrite `RunAsync` (take `success`):

```csharp
    private async Task<bool> RunAsync(string message, string success, Func<Task> work)
    {
        if (!_writes || IsUploading) return false;
        IsBusy = true; BusyMessage = message; ErrorMessage = null;
        using var op = _status.BeginOperation(message);
        try { await work(); await ReloadAsync(); _status.Success(success); return true; }
        catch (IrServiceException ex) { ErrorMessage = ex.Message; _status.Failure(ex.Message); return false; }
        catch (Exception ex)
        {
            Log.Warn(ex, "IR operation failed: {0}", message);
            ErrorMessage = $"Operation failed: {ex.Message}";
            _status.Failure($"Failed: {ex.Message}");
            return false;
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update `RefreshAsync` to report "Reading IRs…":

```csharp
    [RelayCommand] private async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        IsBusy = true; BusyMessage = "Reading IRs…"; ErrorMessage = null;
        using var op = _status.BeginOperation("Reading IRs…");
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            Log.Warn(ex, "IR refresh failed");
            ErrorMessage = $"Refresh failed: {ex.Message}";
            _status.Failure($"Refresh failed: {ex.Message}");
        }
        finally { IsBusy = false; BusyMessage = ""; }
    }
```

Update the `RunAsync` callers:

```csharp
    [RelayCommand] private async Task DeleteAsync()
    {
        if (Selected is { IsEmpty: false } s)
            await RunAsync($"Deleting '{s.Name}'…", $"Deleted '{s.Name}'", () => _irs.DeleteIrAsync(s.Index));
    }

    [RelayCommand] private async Task CommitRenameAsync(IrItemViewModel? item)
    {
        if (item is not { IsEditing: true } s) return;
        var name = (s.EditName ?? "").Trim();
        if (name.Length == 0 || name == s.Name) { s.IsEditing = false; return; }
        if (!await RunAsync($"Renaming '{s.Name}'…", $"Renamed to '{name}'", () => _irs.RenameIrAsync(s.Index, name)))
            s.IsEditing = false;
    }
```

In `StartUploadAsync`, wrap in a status op and report the terminal (IR upload has no auto-close requirement in #5, but reporting is consistent). Add `using var op = _status.BeginOperation($"Uploading '{name}'…");` after the duplicate-name check (before `IsUploading = true;`), add `_status.Success($"Uploaded '{name}' to slot {slot + 1}");` right after `Selected = Items.FirstOrDefault(i => i.Index == slot);`, and add `_status.Failure(ex.Message);` in each typed catch plus `_status.Failure($"Upload failed: {ex.Message}");` in the final `catch (Exception ex)`.

- [ ] **Step 4: Pass `Status` at the construction site**

In `MainWindowViewModel.cs` (~line 125-126):

```csharp
            var irService = new IrService(_connection.Client!, System.IO.Path.Combine("docs", "backups"));
            var irs = new IrListViewModel(irService, _connection.WritesAllowed, Status);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Namager.App.Tests --filter FullyQualifiedName~IrListViewModelTests`
Expected: PASS (existing + new). Then `dotnet build`.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/ViewModels/IrListViewModel.cs src/Namager.App/ViewModels/MainWindowViewModel.cs tests/Namager.App.Tests/IrListViewModelTests.cs
git commit -m "feat(status): IrListViewModel reports operations to the status bar"
```

---

### Task 9: View cleanup — remove per-list indicators, standardize widths, Tone3000 tooltip, amp detail alignment

**Files:**
- Modify: `src/Namager.App/Views/PresetListView.axaml`
- Modify: `src/Namager.App/Views/AmpListView.axaml`
- Modify: `src/Namager.App/Views/IrListView.axaml`
- Modify: `src/Namager.App/Views/Tone3000View.axaml`

This task is `.axaml`-only; verified by `dotnet build` + the manual checklist (Task 10). No unit tests.

- [ ] **Step 1: PresetListView — remove the per-list busy indicator and inline error**

In `src/Namager.App/Views/PresetListView.axaml`, delete the "Busy indicator" `StackPanel` (lines ~38-42) and the "Last operation error" `TextBlock` (lines ~44-48). These are now the global status bar's job. Keep the `IsEnabled="{Binding !IsBusy}"` on the `ListBox` (still valid — `IsBusy` is retained as the gate).

- [ ] **Step 2: AmpListView — remove busy indicator, widen list to 360, align the detail panel**

In `src/Namager.App/Views/AmpListView.axaml`:
- Change the root grid width: `<Grid ColumnDefinitions="260,*">` → `<Grid ColumnDefinitions="360,*">`.
- Delete the busy-indicator `StackPanel` (lines ~28-31).
- Align the detail panel with the list content (fixes "extends too high / too far left"): change `<views:AmpDetailPanel Grid.Column="1" Margin="12,0,0,0"/>` → `<views:AmpDetailPanel Grid.Column="1" Margin="16,34,0,0"/>`. The `34` top offset drops the detail card to line up with the first list row (below the command bar); confirm the exact value on the manual checklist and nudge if needed.

- [ ] **Step 3: IrListView — remove busy indicator, standardize width to 360**

In `src/Namager.App/Views/IrListView.axaml`:
- Change `<DockPanel MaxWidth="560" HorizontalAlignment="Left">` → `<DockPanel MaxWidth="360" HorizontalAlignment="Left">`.
- Delete the busy-indicator `StackPanel` (lines ~68-72). Keep the blocked/error `TextBlock`s at the top for now (the IR upload panel is field-anchored like the amp one) OR, for consistency with the amp panel, leave them — they are not the duplicated *list-op* error. Leave them unchanged.

- [ ] **Step 4: Tone3000View — tooltip on the truncated model name**

In `src/Namager.App/Views/Tone3000View.axaml`, the model-name `TextBlock` (line ~165) is truncated with no tooltip. Add `ToolTip.Tip` bound to the full name:

```xml
                      <TextBlock Grid.Column="0" Text="{Binding Name}" VerticalAlignment="Center"
                                 TextTrimming="CharacterEllipsis"
                                 ToolTip.Tip="{Binding Name}"/>
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Namager.App/Views/PresetListView.axaml src/Namager.App/Views/AmpListView.axaml src/Namager.App/Views/IrListView.axaml src/Namager.App/Views/Tone3000View.axaml
git commit -m "feat(ui): remove per-list indicators, standardize list widths, model tooltip, amp detail alignment (#5)"
```

---

### Task 10: Hardware/visual validation checklist doc

**Files:**
- Create: `docs/HARDWARE-VALIDATION-status-and-polish.md`

This captures the manual checks that unit tests can't cover (real-device status behavior + visual polish). No code; committed as the release's validation record.

- [ ] **Step 1: Write the checklist**

Create `docs/HARDWARE-VALIDATION-status-and-polish.md`:

```markdown
# Hardware / Visual Validation — Status & Polish release

Run with the pedal connected (USB, VoidX-Control CLOSED). Check each item.

## Status bar (#4, #6)
- [ ] At rest before connecting, the bottom bar reads "Not connected".
- [ ] Clicking Connect: the button shows the busy state ("Connecting…" + spinner) and is disabled; the bar shows "Connecting…", then "Reading presets…", then settles to the device summary (name + version + transport).
- [ ] Visiting the Amps tab for the first time shows "Reading amps…" in the bar; the IRs tab shows "Reading IRs…".
- [ ] Saving a preset shows "✓ Saved" briefly (~4s), then the bar returns to the device summary.
- [ ] A preset move/duplicate/delete/rename shows "✓ Moved/Duplicated/Deleted/Renamed …".
- [ ] Force a failure (e.g. unplug mid-op): the bar shows a red "⚠ …" message that persists until the next operation. The app does NOT crash.
- [ ] During a preset copy/reorder, the bar's progress area is visibly active and clears when done — you can tell when the copy finishes.

## Layout polish (#5)
- [ ] The Amps list and IRs list are the same width as the Presets list (360px).
- [ ] The amp detail panel top-aligns with the amp list's first row (not floating above it) and is not pushed too far left.
- [ ] After a successful amp upload, the upload panel closes automatically and the new amp's detail card is shown and selected; the bar shows "✓ Uploaded '…' to slot N".
- [ ] After a FAILED amp upload, the panel stays open with the error visible.
- [ ] On the Tone3000 tab, hovering a truncated model name shows the full name in a tooltip.

## Theming
- [ ] The status bar, success (green) and error (red) colors read correctly in BOTH light and dark themes.
```

- [ ] **Step 2: Commit**

```bash
git add docs/HARDWARE-VALIDATION-status-and-polish.md
git commit -m "docs: hardware/visual validation checklist for the status & polish release"
```

---

## Notes for the implementer

- Run the FULL suite (`dotnet test`) after Task 8 and again after Task 9 to confirm no regression in the 490 existing tests (the constructor-parameter insertions in Tasks 7/8 are the most likely to break an existing positional call — fix those by switching the affected test call to named arguments).
- `IsBusy`/`BusyMessage`/`ErrorMessage` on the list VMs are intentionally retained (they gate `IsEnabled`/`CanRefresh`/`CanMutate` and are asserted by existing tests). This plan moves only their *display* to the global bar; it does not delete the properties.
- Field-anchored inline errors are kept on purpose: the parameter editor's `ErrorMessage` next to Save, and the amp/IR upload panels' `UploadError`. The global bar mirrors failures; it does not replace these.
```
