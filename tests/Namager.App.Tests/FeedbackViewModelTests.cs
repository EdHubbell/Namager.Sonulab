using Namager.App.Services;
using Namager.App.ViewModels;

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

    private sealed class GatedFeedbackService : IFeedbackService
    {
        public readonly TaskCompletionSource Gate = new();
        public Task SendAsync(FeedbackReport report, CancellationToken ct = default) => Gate.Task;
    }

    private static FeedbackViewModel Vm(IFeedbackService? svc = null)
        => new(svc ?? new FakeFeedbackService(), "1.2.3", "Windows 11");

    private static FeedbackViewModel ValidVm(IFeedbackService? svc = null)
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
        vm.Message = "Love it, but...";
        vm.Email = new string('x', 195) + "@example.com";  // 207 chars, over 200 cap
        Assert.False(vm.CanSend);
    }

    [Fact]
    public void Exactly_at_cap_fields_can_send()
    {
        var vm = ValidVm();

        // Exactly 100 chars
        vm.Name = new string('x', 100);
        Assert.True(vm.CanSend);

        // Exactly 200 chars
        vm.Email = new string('x', 188) + "@example.com";  // 188 + 12 = 200
        Assert.True(vm.CanSend);

        // Exactly 4000 chars
        vm.Message = new string('x', 4000);
        Assert.True(vm.CanSend);
    }

    // ---------- send: state machine ----------
    [Fact]
    public async Task Cannot_send_while_sending()
    {
        var svc = new GatedFeedbackService();
        var vm = ValidVm(svc);

        var sendTask = vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(FeedbackState.Sending, vm.State);
        Assert.False(vm.CanSend);

        svc.Gate.SetResult();
        await sendTask;

        Assert.Equal(FeedbackState.Sent, vm.State);
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
