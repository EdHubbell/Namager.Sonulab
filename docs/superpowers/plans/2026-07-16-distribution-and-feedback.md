# Distribution & Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship StompStationManager to users — a tag-triggered CI pipeline that publishes a .msi to GitHub Releases, a real app icon, an in-app feedback button that files GitHub issues via a Cloudflare Worker, and a startup update check.

**Architecture:** Four independent slices. (1) CI/release: GitHub Actions + a WiX v6 installer project consuming a self-contained `dotnet publish`. (2) Icon: SVG master → committed multi-res .ico wired into exe/window/installer. (3) Feedback: `FeedbackViewModel` → `IFeedbackService` → HTTPS POST to a Cloudflare Worker holding a repo-scoped GitHub token. (4) Update check: `UpdateCheckService` hits the public Releases API; a dismissible banner links to the release page.

**Tech Stack:** .NET 10, Avalonia 12.0.4 (built-in FluentTheme), CommunityToolkit.Mvvm 8.4.2, xUnit, WiX v6 (`WixToolset.Sdk`), GitHub Actions (`windows-latest`), Cloudflare Workers (plain JS, no deps), ImageMagick 7 (icon generation, dev machine only).

**Spec:** `docs/superpowers/specs/2026-07-16-distribution-and-feedback-design.md`

## Global Constraints

- .NET 10 / Avalonia **12.0.4** with built-in `FluentTheme`. **Never add FluentAvalonia** (crashes on Avalonia 12).
- No hex color literals in `.axaml` — use the `Sonulab.*` DynamicResource tokens from `Styles/SonulabTheme.axaml`.
- Icons are `StreamGeometry` entries in `src/Sonulab.App/Icons.axaml` used via `PathIcon` — no icon libraries.
- Tests are xUnit in `tests/Sonulab.App.Tests` (file-scoped, no namespace, global `Xunit` using). **No network access in any test** — HTTP services take an injectable `HttpMessageHandler`.
- Repo: `EdHubbell/StompStationManager` (public). Field caps everywhere: name ≤ 100, email ≤ 200, message ≤ 4000.
- Installer `UpgradeCode` is fixed forever: `1431D009-A559-46D0-9568-BD9675EFC753`.
- Local build version is `1.0.0-dev`; CI stamps the real version from the git tag (`v1.2.3` → `1.2.3`). A `-dev` build must never show the update banner.
- Run tests from repo root with `dotnet test --nologo` (~312 existing tests must stay green).
- Commit after each task with a conventional-commit message.

---

### Task 1: Version plumbing (`AppInfo` + csproj + window title)

**Files:**
- Modify: `src/Sonulab.App/Sonulab.App.csproj` (add `<Version>`)
- Create: `src/Sonulab.App/AppInfo.cs`
- Modify: `src/Sonulab.App/Views/MainWindow.axaml.cs` (title)
- Test: `tests/Sonulab.App.Tests/AppInfoTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class AppInfo { static string Version { get; } }` in namespace `Sonulab.App` — used by Tasks 3 (update check), 7 (feedback dialog). Local test/dev builds report `1.0.0-dev`.

- [ ] **Step 1: Write the failing test**

Create `tests/Sonulab.App.Tests/AppInfoTests.cs`:

```csharp
using Sonulab.App;

public class AppInfoTests
{
    [Fact]
    public void Version_is_the_csproj_version_without_sourcelink_hash()
    {
        // Local/test builds carry the csproj default. CI overrides with -p:Version=<tag>.
        Assert.Equal("1.0.0-dev", AppInfo.Version);
        Assert.DoesNotContain("+", AppInfo.Version);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter AppInfoTests`
Expected: FAIL — `AppInfo` does not exist (compile error).

- [ ] **Step 3: Implement**

Create `src/Sonulab.App/AppInfo.cs`:

```csharp
using System.Reflection;

namespace Sonulab.App;

/// <summary>Version of the running app. CI stamps it from the git tag (-p:Version=1.2.3);
/// local builds are "1.0.0-dev". The SDK appends "+&lt;git sha&gt;" to the informational
/// version — stripped here.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
```

In `src/Sonulab.App/Sonulab.App.csproj`, add to the first `<PropertyGroup>` (after `<ApplicationManifest>`):

```xml
    <Version>1.0.0-dev</Version>
```

In `src/Sonulab.App/Views/MainWindow.axaml.cs`, first line of the constructor body (before `InitializeComponent()` is fine):

```csharp
        Title = $"StompStation Manager v{AppInfo.Version}";
```

(add `using Sonulab.App;` only if the namespace isn't already visible — `Sonulab.App.Views` sees it implicitly).

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo`
Expected: all pass, including `AppInfoTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/AppInfo.cs src/Sonulab.App/Sonulab.App.csproj src/Sonulab.App/Views/MainWindow.axaml.cs tests/Sonulab.App.Tests/AppInfoTests.cs
git commit -m "feat: version plumbing - AppInfo, csproj Version, window title shows version"
```

---

### Task 2: CI workflow (build + test on push/PR)

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: nothing.
- Produces: green CI on `main`; Task 11's release workflow assumes the same `dotnet test` invocation works on `windows-latest`.

- [ ] **Step 1: Write the workflow**

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - name: Build & test
        run: dotnet test --nologo
```

- [ ] **Step 2: Verify locally that the command it runs is green**

Run: `dotnet test --nologo`
Expected: all tests pass (this is exactly what the runner will execute).

- [ ] **Step 3: Commit and push, then watch the run**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: build+test workflow on push/PR to main"
git push
gh run watch --exit-status
```

Expected: the `CI` run completes with success. If it fails, fix before proceeding (this gate protects every later task).

---

### Task 3: UpdateCheckService (version compare + Releases API client)

**Files:**
- Create: `src/Sonulab.App/Services/UpdateCheckService.cs`
- Modify: `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`
- Test: `tests/Sonulab.App.Tests/UpdateCheckServiceTests.cs`

**Interfaces:**
- Consumes: `AppInfo.Version` (Task 1).
- Produces (namespace `Sonulab.App.Services`):
  - `sealed record UpdateInfo(string Version, string Url)`
  - `interface IUpdateCheckService { Task<UpdateInfo?> CheckAsync(CancellationToken ct = default); }`
  - `sealed class UpdateCheckService : IUpdateCheckService` with ctor `(HttpMessageHandler? handler = null, string? currentVersion = null)` and `public static bool IsNewer(string current, string latest)`
  - On `MainWindowViewModel`: `UpdateInfo? UpdateAvailable` (observable), `Task CheckForUpdatesAsync(IUpdateCheckService service)`, `IRelayCommand DismissUpdateCommand`. Task 4 binds these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.App.Tests/UpdateCheckServiceTests.cs`:

```csharp
using System.Net;
using Sonulab.App.Services;
using Sonulab.App.ViewModels;

public class UpdateCheckServiceTests
{
    // ---------- pure version compare ----------
    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    [InlineData("1.0.0-dev", "9.9.9", false)]   // dev builds never prompt
    [InlineData("1.0.0", "garbage", false)]     // malformed latest -> never prompt
    [InlineData("garbage", "1.0.0", false)]     // malformed current -> never prompt
    public void IsNewer_compares_correctly(string current, string latest, bool expected)
        => Assert.Equal(expected, UpdateCheckService.IsNewer(current, latest));

    // ---------- HTTP behavior via fake handler ----------
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body) });
    }

    private const string ReleaseJson =
        """{"tag_name":"v2.5.0","html_url":"https://github.com/EdHubbell/StompStationManager/releases/tag/v2.5.0"}""";

    [Fact]
    public async Task CheckAsync_returns_update_when_newer()
    {
        var svc = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson), "1.0.0");
        var info = await svc.CheckAsync();
        Assert.NotNull(info);
        Assert.Equal("2.5.0", info!.Version);
        Assert.Contains("/releases/tag/v2.5.0", info.Url);
    }

    [Fact]
    public async Task CheckAsync_returns_null_when_current_is_latest()
    {
        var svc = new UpdateCheckService(new FakeHandler(HttpStatusCode.OK, ReleaseJson), "2.5.0");
        Assert.Null(await svc.CheckAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "rate limited")]
    [InlineData(HttpStatusCode.NotFound, "no releases yet")]
    public async Task CheckAsync_swallows_all_failures(HttpStatusCode status, string body)
    {
        var svc = new UpdateCheckService(new FakeHandler(status, body), "1.0.0");
        Assert.Null(await svc.CheckAsync());   // must not throw either
    }

    // ---------- MainWindowViewModel wiring ----------
    private sealed class FakeUpdateCheck(UpdateInfo? result) : IUpdateCheckService
    {
        public Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) => Task.FromResult(result);
    }

    [Fact]
    public async Task ViewModel_sets_banner_when_update_found_and_dismiss_clears_it()
    {
        var vm = new MainWindowViewModel();
        await vm.CheckForUpdatesAsync(new FakeUpdateCheck(new UpdateInfo("2.0.0", "https://example.test/rel")));
        Assert.NotNull(vm.UpdateAvailable);
        vm.DismissUpdateCommand.Execute(null);
        Assert.Null(vm.UpdateAvailable);
    }

    [Fact]
    public async Task ViewModel_stays_quiet_when_no_update()
    {
        var vm = new MainWindowViewModel();
        await vm.CheckForUpdatesAsync(new FakeUpdateCheck(null));
        Assert.Null(vm.UpdateAvailable);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter UpdateCheckServiceTests`
Expected: FAIL — `UpdateCheckService` / `UpdateInfo` do not exist (compile error).

- [ ] **Step 3: Implement the service**

Create `src/Sonulab.App/Services/UpdateCheckService.cs`:

```csharp
using System.Net.Http;
using System.Text.Json;

namespace Sonulab.App.Services;

public sealed record UpdateInfo(string Version, string Url);

public interface IUpdateCheckService
{
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);
}

/// <summary>Startup new-version check against the public GitHub Releases API.
/// Unauthenticated (60 req/hr/IP is ample for once-per-launch). Never throws and never
/// blocks startup — every failure mode returns null.</summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/EdHubbell/StompStationManager/releases/latest";

    private readonly HttpClient _http;
    private readonly string _currentVersion;

    public UpdateCheckService(HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StompStationManager");
        _currentVersion = currentVersion ?? AppInfo.Version;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(LatestReleaseUrl, ct));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl) ||
                !doc.RootElement.TryGetProperty("html_url", out var urlEl))
                return null;
            var tag = tagEl.GetString();
            var url = urlEl.GetString();
            if (tag is null || url is null) return null;

            var latest = tag.TrimStart('v', 'V');
            return IsNewer(_currentVersion, latest) ? new UpdateInfo(latest, url) : null;
        }
        catch
        {
            return null;   // offline / rate-limited / malformed: silent no-op by spec
        }
    }

    /// <summary>Dev builds (any '-' suffix, e.g. 1.0.0-dev) never prompt; otherwise
    /// plain System.Version comparison. Unparseable input never prompts.</summary>
    public static bool IsNewer(string current, string latest)
    {
        if (current.Contains('-')) return false;
        return Version.TryParse(current, out var c)
            && Version.TryParse(latest, out var l)
            && l > c;
    }
}
```

- [ ] **Step 4: Wire the ViewModel**

In `src/Sonulab.App/ViewModels/MainWindowViewModel.cs`:

Add usings at the top:

```csharp
using CommunityToolkit.Mvvm.Input;
using Sonulab.App.Services;
```

Add alongside the other `[ObservableProperty]` fields:

```csharp
    [ObservableProperty] private UpdateInfo? _updateAvailable;
```

Add these members after `EnsureTabLoaded` (before the constructor):

```csharp
    /// <summary>Called by the view once the window has opened (fire-and-forget there).
    /// Seam: tests pass a fake; the view passes a real UpdateCheckService.</summary>
    public async Task CheckForUpdatesAsync(IUpdateCheckService service)
        => UpdateAvailable = await service.CheckAsync();

    [RelayCommand]
    private void DismissUpdate() => UpdateAvailable = null;
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --nologo`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.App/Services/UpdateCheckService.cs src/Sonulab.App/ViewModels/MainWindowViewModel.cs tests/Sonulab.App.Tests/UpdateCheckServiceTests.cs
git commit -m "feat: update check service - GitHub releases/latest, silent-fail, dev builds never prompt"
```

---

### Task 4: Update banner UI

**Files:**
- Modify: `src/Sonulab.App/Views/MainWindow.axaml` (banner Border after the top connection bar)
- Modify: `src/Sonulab.App/Views/MainWindow.axaml.cs` (Opened hook + Download click)

**Interfaces:**
- Consumes: `MainWindowViewModel.UpdateAvailable`, `DismissUpdateCommand`, `CheckForUpdatesAsync` (Task 3); `UpdateCheckService` (Task 3).
- Produces: nothing consumed later. Pure view wiring — no VM logic here (it was tested in Task 3), so this task is build-verified.

- [ ] **Step 1: Add the banner to MainWindow.axaml**

In `src/Sonulab.App/Views/MainWindow.axaml`, insert directly AFTER the closing `</Border>` of the "Top connection bar" block (line ~37) and BEFORE the `<!-- ===== SplitView ... -->` comment:

```xml
    <!-- ===== Update-available banner (hidden unless a newer release exists) ===== -->
    <Border DockPanel.Dock="Top" Padding="12,6"
            Background="{DynamicResource Sonulab.SurfaceAltBrush}"
            BorderBrush="{DynamicResource Sonulab.BorderBrush}" BorderThickness="0,0,0,1"
            IsVisible="{Binding UpdateAvailable, Converter={x:Static ObjectConverters.IsNotNull}}">
      <StackPanel Orientation="Horizontal" Spacing="10" VerticalAlignment="Center">
        <TextBlock VerticalAlignment="Center"
                   Text="{Binding UpdateAvailable.Version, StringFormat='Version {0} is available.'}"/>
        <Button Classes="accent-outline" Content="Download" Click="OnDownloadUpdateClick"/>
        <Button Content="Dismiss" Command="{Binding DismissUpdateCommand}"/>
      </StackPanel>
    </Border>
```

- [ ] **Step 2: Hook Opened + Download in code-behind**

In `src/Sonulab.App/Views/MainWindow.axaml.cs`:

Add usings:

```csharp
using Avalonia.Interactivity;
using Sonulab.App.Services;
```

In the constructor, after the existing `DataContextChanged` subscription:

```csharp
        // Update check runs after the window shows so it can never delay startup.
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                _ = vm.CheckForUpdatesAsync(new UpdateCheckService());
        };
```

Add the handler method to the class:

```csharp
    private void OnDownloadUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { UpdateAvailable: { } update })
            _ = Launcher.LaunchUriAsync(new Uri(update.Url));
    }
```

(`Launcher` is inherited from `TopLevel`; `LaunchUriAsync` opens the default browser.)

- [ ] **Step 3: Build + full test run**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: build clean (XAML compiles), all tests pass.

- [ ] **Step 4: Manual eyeball (no device needed)**

Run: `dotnet run --project src/Sonulab.App`
Expected: title shows `StompStation Manager v1.0.0-dev`; NO banner appears (dev build never prompts — this is the negative check). Close the app.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/Views/MainWindow.axaml src/Sonulab.App/Views/MainWindow.axaml.cs
git commit -m "feat: update-available banner with download/dismiss"
```

---

### Task 5: FeedbackViewModel (validation + send state machine)

**Files:**
- Create: `src/Sonulab.App/Services/FeedbackService.cs` (interface + record + exception ONLY in this task — concrete service is Task 6)
- Create: `src/Sonulab.App/ViewModels/FeedbackViewModel.cs`
- Test: `tests/Sonulab.App.Tests/FeedbackViewModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `Sonulab.App.Services`):
  - `sealed record FeedbackReport(string Name, string Email, string Message, string AppVersion, string Os)`
  - `interface IFeedbackService { Task SendAsync(FeedbackReport report, CancellationToken ct = default); }`
  - `sealed class FeedbackSendException : Exception`
- Produces (namespace `Sonulab.App.ViewModels`):
  - `enum FeedbackState { Editing, Sending, Sent, Failed }`
  - `partial class FeedbackViewModel : ObservableObject` — ctor `(IFeedbackService service, string appVersion, string os)`; observable `Name`/`Email`/`Message` (string), `State` (FeedbackState), `Error` (string?); `bool CanSend`; `IAsyncRelayCommand SendCommand`; `event Action? CloseRequested`. Task 7's dialog binds all of these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.App.Tests/FeedbackViewModelTests.cs`:

```csharp
using Sonulab.App.Services;
using Sonulab.App.ViewModels;

public class FeedbackViewModelTests
{
    private sealed class FakeFeedbackService : IFeedbackService
    {
        public FeedbackReport? LastReport;
        public bool Fail;
        public Task SendAsync(FeedbackReport report, CancellationToken ct = default)
        {
            if (Fail) throw new FeedbackSendException("boom");
            LastReport = report;
            return Task.CompletedTask;
        }
    }

    private static FeedbackViewModel Vm(FakeFeedbackService? svc = null)
        => new(svc ?? new FakeFeedbackService(), "1.2.3", "Windows 11");

    private static FeedbackViewModel ValidVm(FakeFeedbackService? svc = null)
    {
        var vm = Vm(svc);
        vm.Name = "Ed"; vm.Email = "ed@gsdware.com"; vm.Message = "Love it, but...";
        return vm;
    }

    // ---------- validation ----------
    [Fact] public void Empty_form_cannot_send() => Assert.False(Vm().CanSend);
    [Fact] public void Valid_form_can_send() => Assert.True(ValidVm().CanSend);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_name_blocks_send(string name)
    { var vm = ValidVm(); vm.Name = name; Assert.False(vm.CanSend); }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]          // no TLD
    [InlineData("a b@c.com")]    // whitespace
    [InlineData("")]
    public void Bad_email_blocks_send(string email)
    { var vm = ValidVm(); vm.Email = email; Assert.False(vm.CanSend); }

    [Fact]
    public void Blank_message_blocks_send()
    { var vm = ValidVm(); vm.Message = " "; Assert.False(vm.CanSend); }

    [Fact]
    public void Over_cap_fields_block_send()
    {
        var vm = ValidVm();
        vm.Name = new string('x', 101);
        Assert.False(vm.CanSend);
        vm.Name = "Ed";
        vm.Message = new string('x', 4001);
        Assert.False(vm.CanSend);
    }

    // ---------- send: success ----------
    [Fact]
    public async Task Send_success_reports_sent_and_requests_close()
    {
        var svc = new FakeFeedbackService();
        var vm = ValidVm(svc);
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(FeedbackState.Sent, vm.State);
        Assert.True(closed);
        Assert.NotNull(svc.LastReport);
        Assert.Equal("Ed", svc.LastReport!.Name);
        Assert.Equal("ed@gsdware.com", svc.LastReport.Email);
        Assert.Equal("1.2.3", svc.LastReport.AppVersion);
        Assert.Equal("Windows 11", svc.LastReport.Os);
    }

    [Fact]
    public async Task Send_trims_whitespace_from_fields()
    {
        var svc = new FakeFeedbackService();
        var vm = Vm(svc);
        vm.Name = "  Ed  "; vm.Email = " ed@gsdware.com "; vm.Message = " hi there ";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("Ed", svc.LastReport!.Name);
        Assert.Equal("ed@gsdware.com", svc.LastReport.Email);
        Assert.Equal("hi there", svc.LastReport.Message);
    }

    // ---------- send: failure ----------
    [Fact]
    public async Task Send_failure_keeps_text_and_shows_retryable_error()
    {
        var vm = ValidVm(new FakeFeedbackService { Fail = true });
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(FeedbackState.Failed, vm.State);
        Assert.False(closed);
        Assert.NotNull(vm.Error);
        Assert.Equal("Love it, but...", vm.Message);   // text preserved
        Assert.True(vm.CanSend);                       // retry allowed
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter FeedbackViewModelTests`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Implement the service contract**

Create `src/Sonulab.App/Services/FeedbackService.cs` (contract only; the HTTP class is Task 6 and lands in this same file):

```csharp
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
```

- [ ] **Step 4: Implement the ViewModel**

Create `src/Sonulab.App/ViewModels/FeedbackViewModel.cs`:

```csharp
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sonulab.App.Services;

namespace Sonulab.App.ViewModels;

public enum FeedbackState { Editing, Sending, Sent, Failed }

/// <summary>Send Feedback dialog: validates name/email/message, posts via IFeedbackService.
/// Failure keeps the typed text so the user can retry.</summary>
public partial class FeedbackViewModel : ObservableObject
{
    public const int NameCap = 100, EmailCap = 200, MessageCap = 4000;
    private static readonly Regex EmailRe =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly IFeedbackService _service;

    public string AppVersion { get; }
    public string Os { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _email = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private FeedbackState _state = FeedbackState.Editing;

    [ObservableProperty] private string? _error;

    /// <summary>Raised on successful send; the dialog closes itself.</summary>
    public event Action? CloseRequested;

    public FeedbackViewModel(IFeedbackService service, string appVersion, string os)
    {
        _service = service;
        AppVersion = appVersion;
        Os = os;
    }

    public bool CanSend =>
        State != FeedbackState.Sending
        && Name.Trim().Length is > 0 and <= NameCap
        && Email.Trim().Length <= EmailCap && EmailRe.IsMatch(Email.Trim())
        && Message.Trim().Length is > 0 and <= MessageCap;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        State = FeedbackState.Sending;
        Error = null;
        try
        {
            await _service.SendAsync(new FeedbackReport(
                Name.Trim(), Email.Trim(), Message.Trim(), AppVersion, Os));
            State = FeedbackState.Sent;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            State = FeedbackState.Failed;
            Error = "Couldn't send feedback — check your connection and try again.";
            Log.Warn(ex, "feedback send failed");
        }
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --nologo`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/Sonulab.App/Services/FeedbackService.cs src/Sonulab.App/ViewModels/FeedbackViewModel.cs tests/Sonulab.App.Tests/FeedbackViewModelTests.cs
git commit -m "feat: feedback viewmodel - validation, send state machine, retry-preserving failure"
```

---

### Task 6: FeedbackService (HTTP client for the worker)

**Files:**
- Modify: `src/Sonulab.App/Services/FeedbackService.cs` (append the concrete class)
- Test: `tests/Sonulab.App.Tests/FeedbackServiceTests.cs`

**Interfaces:**
- Consumes: `FeedbackReport`, `IFeedbackService`, `FeedbackSendException` (Task 5).
- Produces: `sealed class FeedbackService : IFeedbackService` with ctor `(HttpMessageHandler? handler = null, string? endpoint = null)` and `public const string EndpointUrl`. Task 7 constructs it with no args. JSON wire shape (worker contract, Task 8): `{ name, email, message, appVersion, os, website }` where `website` is always `""` (honeypot).

- [ ] **Step 1: Write the failing tests**

Create `tests/Sonulab.App.Tests/FeedbackServiceTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Sonulab.App.Services;

public class FeedbackServiceTests
{
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body;
        public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private static readonly FeedbackReport Report =
        new("Ed", "ed@gsdware.com", "Great app", "1.2.3", "Windows 11");

    [Fact]
    public async Task Posts_json_with_all_fields_and_empty_honeypot()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created);
        var svc = new FeedbackService(handler, "https://feedback.example.test/");

        await svc.SendAsync(Report);

        Assert.Equal("https://feedback.example.test/", handler.Uri!.ToString());
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("Ed", root.GetProperty("name").GetString());
        Assert.Equal("ed@gsdware.com", root.GetProperty("email").GetString());
        Assert.Equal("Great app", root.GetProperty("message").GetString());
        Assert.Equal("1.2.3", root.GetProperty("appVersion").GetString());
        Assert.Equal("Windows 11", root.GetProperty("os").GetString());
        Assert.Equal("", root.GetProperty("website").GetString());   // honeypot always empty
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Non_success_status_throws_FeedbackSendException(HttpStatusCode status)
    {
        var svc = new FeedbackService(new CapturingHandler(status), "https://feedback.example.test/");
        await Assert.ThrowsAsync<FeedbackSendException>(() => svc.SendAsync(Report));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("dns failure");
    }

    [Fact]
    public async Task Network_error_wraps_into_FeedbackSendException()
    {
        var svc = new FeedbackService(new ThrowingHandler(), "https://feedback.example.test/");
        await Assert.ThrowsAsync<FeedbackSendException>(() => svc.SendAsync(Report));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Sonulab.App.Tests --nologo --filter FeedbackServiceTests`
Expected: FAIL — `FeedbackService` class does not exist (compile error).

- [ ] **Step 3: Implement**

Append to `src/Sonulab.App/Services/FeedbackService.cs` (add `using System.Net.Http;` and `using System.Net.Http.Json;` at the top of the file):

```csharp
/// <summary>Delivers feedback to the Cloudflare Worker (infra/feedback-worker), which
/// creates the GitHub issue. The endpoint URL is public knowledge, not a secret.</summary>
public sealed class FeedbackService : IFeedbackService
{
    /// <summary>Deployed worker endpoint. If `wrangler deploy` reports a different URL
    /// (workers.dev subdomain is per-account), update this constant to match.</summary>
    public const string EndpointUrl = "https://stompstation-feedback.edhubbell.workers.dev/";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public FeedbackService(HttpMessageHandler? handler = null, string? endpoint = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _endpoint = endpoint ?? EndpointUrl;
    }

    public async Task SendAsync(FeedbackReport report, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(_endpoint, new
            {
                name = report.Name,
                email = report.Email,
                message = report.Message,
                appVersion = report.AppVersion,
                os = report.Os,
                website = "",                    // honeypot: real clients always send empty
            }, ct);
            if (!resp.IsSuccessStatusCode)
                throw new FeedbackSendException($"feedback endpoint returned {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FeedbackSendException("feedback endpoint unreachable", ex);
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --nologo`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.App/Services/FeedbackService.cs tests/Sonulab.App.Tests/FeedbackServiceTests.cs
git commit -m "feat: feedback http service - posts to cloudflare worker with honeypot field"
```

---

### Task 7: Feedback dialog + nav button

**Files:**
- Modify: `src/Sonulab.App/Icons.axaml` (add `Icon.Feedback`)
- Create: `src/Sonulab.App/Views/FeedbackDialog.axaml`
- Create: `src/Sonulab.App/Views/FeedbackDialog.axaml.cs`
- Modify: `src/Sonulab.App/Views/MainWindow.axaml` (nav pane DockPanel + button)
- Modify: `src/Sonulab.App/Views/MainWindow.axaml.cs` (click handler)

**Interfaces:**
- Consumes: `FeedbackViewModel` (Task 5), `FeedbackService` (Task 6), `AppInfo.Version` (Task 1).
- Produces: nothing consumed later. View-only task; VM logic already tested — build-verify + manual eyeball here. **Nav ListBox indices must not change** (the button is OUTSIDE the ListBox) — `OnNavSelectionChanged` and Tone3000 handoff depend on indices 0/1/2/4.

- [ ] **Step 1: Add the feedback icon geometry**

In `src/Sonulab.App/Icons.axaml`, add before `</ResourceDictionary>`:

```xml
  <StreamGeometry x:Key="Icon.Feedback">M20 2H4c-1.1 0-1.99.9-1.99 2L2 22l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-2 12H6v-2h12v2zm0-3H6V9h12v2zm0-3H6V6h12v2z</StreamGeometry>
```

- [ ] **Step 2: Create the dialog view**

Create `src/Sonulab.App/Views/FeedbackDialog.axaml`:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:Sonulab.App.ViewModels"
        x:Class="Sonulab.App.Views.FeedbackDialog"
        x:DataType="vm:FeedbackViewModel"
        Title="Send Feedback"
        Width="480" SizeToContent="Height" CanResize="False"
        WindowStartupLocation="CenterOwner">

  <StackPanel Margin="20" Spacing="10">

    <TextBlock Text="Tell us what's working and what isn't." FontWeight="SemiBold"/>

    <TextBlock TextWrapping="Wrap" Opacity="0.7" FontSize="12"
               Text="Your feedback — including your name and email — will be posted as a public GitHub issue."/>

    <TextBlock Text="Name"/>
    <TextBox Text="{Binding Name}" MaxLength="100" Watermark="Your name"/>

    <TextBlock Text="Email"/>
    <TextBox Text="{Binding Email}" MaxLength="200" Watermark="you@example.com"/>

    <TextBlock Text="Message"/>
    <TextBox Text="{Binding Message}" MaxLength="4000" Watermark="What happened? What would make the app better?"
             AcceptsReturn="True" TextWrapping="Wrap" MinHeight="120" MaxHeight="240"/>

    <TextBlock Opacity="0.6" FontSize="11">
      <TextBlock.Text>
        <MultiBinding StringFormat="Sent with app version {0} on {1}.">
          <Binding Path="AppVersion"/>
          <Binding Path="Os"/>
        </MultiBinding>
      </TextBlock.Text>
    </TextBlock>

    <TextBlock Text="{Binding Error}" IsVisible="{Binding Error, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"
               Foreground="{DynamicResource Sonulab.DangerBrush}" TextWrapping="Wrap"/>

    <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
      <ProgressBar IsIndeterminate="True" Width="80" VerticalAlignment="Center"
                   IsVisible="{Binding State, Converter={x:Static ObjectConverters.Equal}, ConverterParameter={x:Static vm:FeedbackState.Sending}}"/>
      <Button Content="Cancel" Click="OnCancelClick"/>
      <Button Classes="accent-outline" Content="Send" Command="{Binding SendCommand}"/>
    </StackPanel>

  </StackPanel>
</Window>
```

> Note for the implementer: if `Sonulab.DangerBrush` does not exist in `Styles/SonulabTheme.axaml`, check that file for the error/danger token actually defined (e.g. `Sonulab.ErrorBrush`) and use that token — do NOT introduce a hex literal. If no danger token exists at all, add `Sonulab.DangerBrush` to both theme variants in `SonulabTheme.axaml` reusing the palette's existing red/warning values.

Create `src/Sonulab.App/Views/FeedbackDialog.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sonulab.App.ViewModels;

namespace Sonulab.App.Views;

public partial class FeedbackDialog : Window
{
    public FeedbackDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is FeedbackViewModel vm)
                vm.CloseRequested += Close;
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 3: Add the nav-pane button**

In `src/Sonulab.App/Views/MainWindow.axaml`, replace the `<SplitView.Pane>` block (currently just the `ListBox`) so the pane is a `DockPanel` with the feedback button docked at the bottom and the existing `ListBox` (UNCHANGED, including all its items) filling the rest:

```xml
      <SplitView.Pane>
        <DockPanel>
          <Button DockPanel.Dock="Bottom" Margin="4,0,4,8"
                  HorizontalAlignment="Stretch" HorizontalContentAlignment="Left"
                  Background="Transparent" BorderThickness="0"
                  Click="OnFeedbackClick" ToolTip.Tip="Report a problem or suggest an improvement">
            <StackPanel Orientation="Horizontal" Spacing="8" Margin="8,4">
              <PathIcon Data="{StaticResource Icon.Feedback}" Width="16" Height="16"/>
              <TextBlock Text="Send Feedback" VerticalAlignment="Center"/>
            </StackPanel>
          </Button>
          <ListBox x:Name="NavList" SelectionMode="Single" SelectedIndex="0" Margin="4">
            <!-- ... keep the existing five ListBoxItems exactly as they are ... -->
          </ListBox>
        </DockPanel>
      </SplitView.Pane>
```

(The comment above is shorthand for THIS PLAN only — in the real file, keep the existing ListBox content verbatim; only wrap it in the DockPanel and add the Button.)

- [ ] **Step 4: Add the click handler**

In `src/Sonulab.App/Views/MainWindow.axaml.cs`, add the method:

```csharp
    private async void OnFeedbackClick(object? sender, RoutedEventArgs e)
    {
        var vm = new FeedbackViewModel(
            new FeedbackService(),
            AppInfo.Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        await new FeedbackDialog { DataContext = vm }.ShowDialog(this);
    }
```

- [ ] **Step 5: Build + tests**

Run: `dotnet build --nologo && dotnet test --nologo`
Expected: clean build, all tests pass.

- [ ] **Step 6: Manual eyeball**

Run: `dotnet run --project src/Sonulab.App`
Expected: "Send Feedback" sits at the bottom of the nav pane; clicking opens the dialog centered on the window; Send is disabled until name + valid email + message are filled; Cancel closes. (Actually sending will fail until the worker is deployed — expected; verify the inline error appears and the text is NOT lost.)

- [ ] **Step 7: Commit**

```bash
git add src/Sonulab.App/Icons.axaml src/Sonulab.App/Views/FeedbackDialog.axaml src/Sonulab.App/Views/FeedbackDialog.axaml.cs src/Sonulab.App/Views/MainWindow.axaml src/Sonulab.App/Views/MainWindow.axaml.cs
git commit -m "feat: send-feedback dialog and nav button"
```

---

### Task 8: Cloudflare Worker + deployment doc

**Files:**
- Create: `infra/feedback-worker/worker.js`
- Create: `infra/feedback-worker/wrangler.toml`
- Create: `infra/feedback-worker/README.md`

**Interfaces:**
- Consumes: the JSON wire shape from Task 6: `{ name, email, message, appVersion, os, website }`.
- Produces: HTTP 201 on success; 4xx/5xx otherwise. Creates GitHub issues labeled `user-feedback` on `EdHubbell/StompStationManager`. Deployment is MANUAL (Ed runs it) — CI never touches this directory.

- [ ] **Step 1: Write the worker**

Create `infra/feedback-worker/worker.js`:

```js
// StompStationManager feedback endpoint: turns an app POST into a GitHub issue.
// Deployed manually with `wrangler deploy`; secret GITHUB_TOKEN is a fine-grained PAT
// scoped to EdHubbell/StompStationManager with Issues read/write ONLY.

const REPO = 'EdHubbell/StompStationManager';
const CAPS = { name: 100, email: 200, message: 4000 };
const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

// Best-effort per-isolate rate limit (resets when the isolate recycles).
// The real backstop is the Cloudflare dashboard rate-limiting rule (see README).
const hits = new Map();
const MAX_PER_HOUR = 5;

export default {
  async fetch(request, env) {
    if (request.method !== 'POST')
      return new Response('method not allowed', { status: 405 });
    if (!(request.headers.get('content-type') || '').includes('application/json'))
      return new Response('unsupported content type', { status: 415 });

    let f;
    try { f = await request.json(); } catch { return new Response('bad json', { status: 400 }); }

    // Honeypot: bots fill every field. Pretend success so they don't adapt.
    if (f.website) return new Response(null, { status: 201 });

    for (const [field, max] of Object.entries(CAPS)) {
      if (typeof f[field] !== 'string' || !f[field].trim() || f[field].length > max)
        return new Response(`invalid ${field}`, { status: 400 });
    }
    if (!EMAIL_RE.test(f.email))
      return new Response('invalid email', { status: 400 });

    const ip = request.headers.get('cf-connecting-ip') || 'unknown';
    const now = Date.now();
    const recent = (hits.get(ip) || []).filter(t => now - t < 3600_000);
    if (recent.length >= MAX_PER_HOUR)
      return new Response('rate limited', { status: 429 });
    hits.set(ip, [...recent, now]);

    const title = `Feedback: ${f.message.trim().slice(0, 60)}`;
    const body = [
      f.message.trim(),
      '',
      '---',
      `**Name:** ${f.name.trim()}`,
      `**Email:** ${f.email.trim()}`,
      `**App version:** ${typeof f.appVersion === 'string' ? f.appVersion.slice(0, 50) : 'unknown'}`,
      `**OS:** ${typeof f.os === 'string' ? f.os.slice(0, 200) : 'unknown'}`,
    ].join('\n');

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'accept': 'application/vnd.github+json',
        'user-agent': 'stompstation-feedback-worker',
        'content-type': 'application/json',
      },
      body: JSON.stringify({ title, body, labels: ['user-feedback'] }),
    });

    // Never leak GitHub response details (or the token's existence) to callers.
    return new Response(null, { status: gh.status === 201 ? 201 : 502 });
  },
};
```

- [ ] **Step 2: Write the wrangler config**

Create `infra/feedback-worker/wrangler.toml`:

```toml
name = "stompstation-feedback"
main = "worker.js"
compatibility_date = "2026-07-01"
```

- [ ] **Step 3: Write the deployment README**

Create `infra/feedback-worker/README.md`:

````markdown
# Feedback worker

Receives `{ name, email, message, appVersion, os, website }` POSTs from the app's
Send Feedback dialog and creates a GitHub issue labeled `user-feedback` on
`EdHubbell/StompStationManager`. `website` is a honeypot — always empty from the real app.

**This worker is deployed manually, never by CI.**

## One-time setup (Ed)

1. **Create the fine-grained GitHub PAT**
   github.com → Settings → Developer settings → Fine-grained tokens → Generate new token.
   - Repository access: *Only select repositories* → `StompStationManager`
   - Permissions: *Issues: Read and write*. Nothing else.
   - Expiration: 1 year (calendar a renewal reminder).
   Copy the token.

2. **Create the issue label** (once):
   ```
   gh label create user-feedback --repo EdHubbell/StompStationManager --color F9D0C4 --description "Submitted from the app's Send Feedback dialog"
   ```

3. **Deploy the worker** (needs Node; no install required):
   ```
   cd infra/feedback-worker
   npx wrangler login          # first time only: opens browser to your Cloudflare account
   npx wrangler deploy
   npx wrangler secret put GITHUB_TOKEN    # paste the PAT when prompted
   ```
   `deploy` prints the live URL, e.g. `https://stompstation-feedback.<your-subdomain>.workers.dev`.

4. **Sync the app**: if the printed URL differs from `FeedbackService.EndpointUrl`
   (`src/Sonulab.App/Services/FeedbackService.cs`), update that constant and commit.

5. **Rate-limit backstop** (recommended): Cloudflare dashboard → Workers & Pages →
   stompstation-feedback → Settings → add a rate limiting rule, e.g. 10 requests per
   minute per IP. (The worker also self-limits to 5/hour/IP, best-effort.)

## Smoke tests

Local (`npx wrangler dev`, then against http://localhost:8787) or against the live URL:

```bash
# happy path -> 201 and a new issue appears
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"Test","email":"t@example.com","message":"smoke test - please close","appVersion":"0.0.0","os":"curl","website":""}'

# honeypot filled -> 201 but NO issue created
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"Bot","email":"b@example.com","message":"spam","website":"http://spam"}'

# bad email -> 400
curl -si -X POST <URL> -H "content-type: application/json" \
  -d '{"name":"T","email":"nope","message":"x","website":""}'

# GET -> 405
curl -si <URL>
```

(For `wrangler dev`, put the PAT in `infra/feedback-worker/.dev.vars` as
`GITHUB_TOKEN=...` — that file is gitignored, never commit it.)
````

- [ ] **Step 4: Gitignore the dev secrets file**

Append to the repo root `.gitignore`:

```
infra/feedback-worker/.dev.vars
```

- [ ] **Step 5: Commit**

```bash
git add infra/feedback-worker/ .gitignore
git commit -m "feat: cloudflare feedback worker - validation, honeypot, rate limit, issue creation"
```

*(Live smoke tests run when Ed deploys — recorded in the Task 11 validation checklist.)*

---

### Task 9: Application icon (HUMAN GATE: Ed picks the variant)

**Files:**
- Create: `src/Sonulab.App/Assets/app-icon.svg` (the chosen variant)
- Create: `src/Sonulab.App/Assets/app-icon.ico` (generated)
- Create: `tools/icon/make-ico.ps1`
- Modify: `src/Sonulab.App/Sonulab.App.csproj` (`ApplicationIcon`)
- Modify: `src/Sonulab.App/Views/MainWindow.axaml` (window `Icon`)
- Delete: `src/Sonulab.App/Assets/avalonia-logo.ico`

**Interfaces:**
- Consumes: nothing.
- Produces: `src/Sonulab.App/Assets/app-icon.ico` — Task 10's installer references this exact path.

- [ ] **Step 1: Render the three variants for Ed**

Write the three SVGs below to the scratchpad (NOT the repo), render each to a 256px and a 16px PNG with ImageMagick, and send all six images to Ed with SendUserFile (or equivalent), asking which variant to use. **STOP and wait for Ed's pick before continuing.**

Variant A — dark pedal, amber bolt over the body:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="48" y="20" width="160" height="216" rx="24" fill="#2E2A26" stroke="#57504A" stroke-width="8"/>
  <circle cx="94" cy="62" r="18" fill="#CFC7BC"/>
  <circle cx="162" cy="62" r="18" fill="#CFC7BC"/>
  <rect x="91" y="46" width="6" height="16" rx="3" fill="#2E2A26"/>
  <rect x="159" y="46" width="6" height="16" rx="3" fill="#2E2A26"/>
  <circle cx="128" cy="184" r="30" fill="#CFC7BC"/>
  <circle cx="128" cy="184" r="20" fill="#8F877D"/>
  <path d="M170 8 L84 130 L122 130 L86 248 L180 108 L140 108 Z"
        fill="#E8A33D" stroke="#1C1916" stroke-width="8" stroke-linejoin="round"/>
</svg>
```

Variant B — amber pedal, dark bolt:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="48" y="20" width="160" height="216" rx="24" fill="#E8A33D" stroke="#9C6A1E" stroke-width="8"/>
  <circle cx="94" cy="62" r="18" fill="#2E2A26"/>
  <circle cx="162" cy="62" r="18" fill="#2E2A26"/>
  <rect x="91" y="46" width="6" height="16" rx="3" fill="#E8A33D"/>
  <rect x="159" y="46" width="6" height="16" rx="3" fill="#E8A33D"/>
  <circle cx="128" cy="184" r="30" fill="#2E2A26"/>
  <circle cx="128" cy="184" r="20" fill="#57504A"/>
  <path d="M170 8 L84 130 L122 130 L86 248 L180 108 L140 108 Z"
        fill="#2E2A26" stroke="#F5E6CC" stroke-width="8" stroke-linejoin="round"/>
</svg>
```

Variant C — badge style: rounded-square dark badge, light pedal silhouette, amber bolt:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="48" fill="#1C1916"/>
  <rect x="72" y="40" width="112" height="176" rx="18" fill="none" stroke="#CFC7BC" stroke-width="10"/>
  <circle cx="104" cy="76" r="12" fill="#CFC7BC"/>
  <circle cx="152" cy="76" r="12" fill="#CFC7BC"/>
  <circle cx="128" cy="176" r="20" fill="#CFC7BC"/>
  <path d="M164 24 L92 128 L124 128 L94 232 L172 110 L138 110 Z"
        fill="#E8A33D" stroke="#1C1916" stroke-width="6" stroke-linejoin="round"/>
</svg>
```

Render commands (scratchpad):

```powershell
magick -background none variant-A.svg -resize 256x256 variant-A-256.png
magick -background none variant-A.svg -resize 16x16 variant-A-16.png
# ...same for B and C
```

- [ ] **Step 2: Commit the chosen SVG + generation script**

Save Ed's chosen variant (with any tweaks he asks for) as `src/Sonulab.App/Assets/app-icon.svg`.

Create `tools/icon/make-ico.ps1`:

```powershell
# Regenerates src/Sonulab.App/Assets/app-icon.ico from app-icon.svg.
# Requires ImageMagick 7+ on PATH (dev machine only - the .ico is committed).
$assets = Join-Path $PSScriptRoot "..\..\src\Sonulab.App\Assets"
magick -background none (Join-Path $assets "app-icon.svg") `
    -define icon:auto-resize=256,128,64,48,32,24,16 `
    (Join-Path $assets "app-icon.ico")
Write-Host "Wrote $((Join-Path $assets 'app-icon.ico'))"
```

Run it: `pwsh tools/icon/make-ico.ps1`
Expected: `app-icon.ico` exists; `magick identify src/Sonulab.App/Assets/app-icon.ico` lists 7 sizes (256 down to 16).

- [ ] **Step 3: Wire the icon in**

In `src/Sonulab.App/Sonulab.App.csproj`, add to the first `<PropertyGroup>`:

```xml
    <ApplicationIcon>Assets\app-icon.ico</ApplicationIcon>
```

In `src/Sonulab.App/Views/MainWindow.axaml`, add to the `<Window ...>` attributes:

```xml
        Icon="avares://Sonulab.App/Assets/app-icon.ico"
```

Delete the stock icon:

```bash
git rm src/Sonulab.App/Assets/avalonia-logo.ico
```

(Verify first that nothing references it: `grep -r "avalonia-logo" src/` must return nothing after the MainWindow edit.)

- [ ] **Step 4: Build + eyeball**

Run: `dotnet build --nologo && dotnet run --project src/Sonulab.App`
Expected: pedal-with-bolt icon in the title bar and taskbar. Check `src/Sonulab.App/bin/Debug/net10.0/Sonulab.App.exe` in Explorer — the exe shows the icon too.

- [ ] **Step 5: Run tests + commit**

Run: `dotnet test --nologo`
Expected: all pass.

```bash
git add src/Sonulab.App/Assets/app-icon.svg src/Sonulab.App/Assets/app-icon.ico tools/icon/make-ico.ps1 src/Sonulab.App/Sonulab.App.csproj src/Sonulab.App/Views/MainWindow.axaml
git commit -m "feat: app icon - guitar pedal with lightning bolt (svg master + multi-res ico)"
```

---

### Task 10: WiX installer project

**Files:**
- Create: `src/Sonulab.Installer/Sonulab.Installer.wixproj`
- Create: `src/Sonulab.Installer/Package.wxs`

**Interfaces:**
- Consumes: `dotnet publish` output at `src/Sonulab.App/bin/Release/net10.0/win-x64/publish`; `app-icon.ico` (Task 9).
- Produces: `src/Sonulab.Installer/bin/x64/Release/StompStationManager-<version>-x64.msi`. Task 11's workflow builds it with `-p:ProductVersion=<version>`. **NOT added to `Sonulab.slnx`** — built explicitly, never by solution-wide `dotnet build`/`dotnet test`.

- [ ] **Step 1: Create the wixproj**

Create `src/Sonulab.Installer/Sonulab.Installer.wixproj`:

```xml
<!-- Deliberately NOT in Sonulab.slnx: packaging runs only in the release workflow or
     via an explicit local build (see step 3), never on plain solution builds. -->
<Project Sdk="WixToolset.Sdk/6.0.1">
  <PropertyGroup>
    <OutputType>Package</OutputType>
    <InstallerPlatform>x64</InstallerPlatform>
    <!-- MSI ProductVersion must be numeric x.y.z - CI passes -p:ProductVersion=<tag>. -->
    <ProductVersion Condition="'$(ProductVersion)' == ''">1.0.0</ProductVersion>
    <PublishDir Condition="'$(PublishDir)' == ''">..\Sonulab.App\bin\Release\net10.0\win-x64\publish</PublishDir>
    <DefineConstants>$(DefineConstants);ProductVersion=$(ProductVersion);PublishDir=$(PublishDir)</DefineConstants>
    <OutputName>StompStationManager-$(ProductVersion)-x64</OutputName>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Create Package.wxs**

Create `src/Sonulab.Installer/Package.wxs`:

```xml
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <!-- Per-user install: no admin/UAC, lands in %LOCALAPPDATA%\Programs\StompStationManager.
       UpgradeCode is FOREVER - changing it breaks in-place upgrades for every user. -->
  <Package Name="StompStation Manager"
           Manufacturer="Ed Hubbell"
           Version="$(var.ProductVersion)"
           UpgradeCode="1431D009-A559-46D0-9568-BD9675EFC753"
           Scope="perUser"
           Language="1033">

    <MajorUpgrade DowngradeErrorMessage="A newer version of StompStation Manager is already installed." />
    <MediaTemplate EmbedCab="yes" />

    <Icon Id="AppIcon" SourceFile="..\Sonulab.App\Assets\app-icon.ico" />
    <Property Id="ARPPRODUCTICON" Value="AppIcon" />
    <Property Id="ARPURLINFOABOUT" Value="https://github.com/EdHubbell/StompStationManager" />

    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="ProgramsDir" Name="Programs">
        <Directory Id="INSTALLFOLDER" Name="StompStationManager">
          <!-- Everything dotnet publish emitted, subfolders included. -->
          <Files Include="$(var.PublishDir)\**" />
        </Directory>
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Component Id="StartMenuShortcut">
        <Shortcut Id="AppShortcut"
                  Name="StompStation Manager"
                  Target="[INSTALLFOLDER]Sonulab.App.exe"
                  WorkingDirectory="INSTALLFOLDER"
                  Icon="AppIcon" />
        <RegistryValue Root="HKCU" Key="Software\StompStationManager"
                       Name="installed" Type="integer" Value="1" KeyPath="yes" />
        <RemoveFolder Id="CleanupShortcut" Directory="ProgramMenuFolder" On="uninstall" />
      </Component>
    </StandardDirectory>

  </Package>
</Wix>
```

- [ ] **Step 3: Build locally and verify**

```powershell
dotnet publish src/Sonulab.App -c Release -r win-x64 --self-contained true -p:Version=1.0.0
dotnet build src/Sonulab.Installer -c Release -p:ProductVersion=1.0.0
```

Expected: `src/Sonulab.Installer/bin/x64/Release/StompStationManager-1.0.0-x64.msi` exists (check with `ls`). If WiX emits path/syntax errors, fix them now — this exact invocation is what CI runs.

- [ ] **Step 4: Manual install smoke test (Ed's machine, optional but recommended)**

Double-click the .msi (or `msiexec /i <msi>`): expect no UAC prompt, app in Start Menu with the pedal icon, launches fine from `%LOCALAPPDATA%\Programs\StompStationManager`. Then uninstall from Settings → Apps: folder and shortcut removed. (Full checklist runs in Task 11's dry run.)

- [ ] **Step 5: Commit**

```bash
git add src/Sonulab.Installer/
git commit -m "feat: wix v6 msi installer - per-user, start menu shortcut, major upgrade"
```

---

### Task 11: Release workflow + docs + validation checklist

**Files:**
- Create: `.github/workflows/release.yml`
- Create: `docs/HARDWARE-VALIDATION-installer.md`
- Modify: `README.md` (Install + Feedback sections)

**Interfaces:**
- Consumes: everything — this is the integration task. `dotnet publish`/installer invocations must match Task 10 step 3 exactly.
- Produces: tag `vX.Y.Z` → GitHub Release with the .msi attached.

- [ ] **Step 1: Write the release workflow**

Create `.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    tags: ['v*']

permissions:
  contents: write   # create the release

jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: Derive version from tag
        run: echo "VERSION=$('${{ github.ref_name }}'.TrimStart('v'))" >> $env:GITHUB_ENV

      - name: Test (release gate)
        run: dotnet test --nologo

      - name: Publish app (self-contained win-x64)
        run: dotnet publish src/Sonulab.App -c Release -r win-x64 --self-contained true -p:Version=$env:VERSION

      - name: Build installer
        run: dotnet build src/Sonulab.Installer -c Release -p:ProductVersion=$env:VERSION

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: src/Sonulab.Installer/bin/**/*.msi
          generate_release_notes: true
          body: |
            ### Install
            Download the `.msi` below and run it. Windows SmartScreen will warn because the
            installer is unsigned — click **More info → Run anyway**. No admin rights and no
            .NET install needed.
```

- [ ] **Step 2: Add README sections**

In `README.md`, add near the top (after the project description):

```markdown
## Install

1. Grab the latest `.msi` from the [Releases page](https://github.com/EdHubbell/StompStationManager/releases/latest).
2. Run it. Windows SmartScreen will warn that the app is unrecognized (the installer is
   unsigned) — click **More info → Run anyway**. Installation is per-user: no admin rights
   needed, and .NET does not need to be installed.
3. Launch **StompStation Manager** from the Start Menu. Close VoidX-Control first — it
   holds the pedal's COM port.

Updating: the app tells you when a new version is available; download and run the new
`.msi` and it upgrades in place.

## Feedback

Use **Send Feedback** (bottom-left in the app). Your message — including the name and
email you enter — is posted as a public [GitHub issue](https://github.com/EdHubbell/StompStationManager/issues)
so you can follow the discussion.
```

- [ ] **Step 3: Write the validation checklist**

Create `docs/HARDWARE-VALIDATION-installer.md`:

```markdown
# Manual validation — installer, feedback, update check

Run after the first successful tag build (dry run: tag `v0.9.0`).

## One-time prerequisites
- [ ] Feedback worker deployed + secret set (infra/feedback-worker/README.md steps 1-5)
- [ ] Worker smoke tests pass (curl matrix in that README)
- [ ] `FeedbackService.EndpointUrl` matches the deployed worker URL

## Release pipeline (tag v0.9.0, push, watch Actions)
- [ ] `git tag v0.9.0 && git push origin v0.9.0` → Release workflow goes green
- [ ] Release page shows `StompStationManager-0.9.0-x64.msi` + SmartScreen note in body

## Installer (on a machine or fresh Windows account, ideally NOT the dev box)
- [ ] Download .msi from the release page; SmartScreen appears; More info → Run anyway works
- [ ] Install completes with NO admin/UAC prompt
- [ ] Start Menu entry "StompStation Manager" with the pedal icon; window/taskbar icon correct
- [ ] Title bar shows "StompStation Manager v0.9.0"
- [ ] App connects to the pedal and lists presets (core function intact in packaged build)
- [ ] Re-run same .msi → repair/no-op, not a second install
- [ ] Settings → Apps shows one entry with icon; uninstall removes app folder + shortcut

## Upgrade path
- [ ] Tag `v0.9.1`, install its .msi OVER v0.9.0 → version in title updates, single Apps entry

## Feedback end-to-end
- [ ] Send Feedback from the installed app → issue appears with `user-feedback` label,
      name/email/version/OS block intact
- [ ] Disconnect network, send → inline error, typed text preserved, retry works after reconnect

## Update check
- [ ] With v0.9.0 installed and v0.9.1 released: launch → banner "Version 0.9.1 is available."
- [ ] Download opens the release page; Dismiss hides banner for the session
- [ ] Delete the v0.9.0/v0.9.1 releases + tags after validation (keep the repo clean for v1.0.0)
```

- [ ] **Step 4: Commit and push**

```bash
git add .github/workflows/release.yml docs/HARDWARE-VALIDATION-installer.md README.md
git commit -m "ci: tag-triggered release workflow; docs: install/feedback README + validation checklist"
git push
```

- [ ] **Step 5: Dry run (with Ed)**

The v0.9.0 dry run needs the worker deployed (Ed's manual step) and is driven by `docs/HARDWARE-VALIDATION-installer.md`. Announce readiness to Ed rather than tagging autonomously if the worker isn't deployed yet — the release itself is safe either way (feedback simply errors politely until the worker exists).
```

---

## Execution notes

- Task order matters: 1 → 2 → (3,4) → (5,6,7) → 8 → 9 → 10 → 11. Tasks 5–8 and 9–10 are independent groups and could swap, but 9 must precede 10 (installer references the .ico).
- Task 9 contains a **human gate** (Ed picks the icon variant) — do not proceed past its Step 1 without his answer.
- Task 11 Step 5 (tag dry run) requires Ed's worker deployment first; everything else is autonomous.
