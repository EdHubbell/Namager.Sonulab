using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ToneManager.App.Services;

namespace ToneManager.App.ViewModels;

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
