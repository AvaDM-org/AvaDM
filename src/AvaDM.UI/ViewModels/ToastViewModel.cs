using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>One transient toast/snackbar notification (design.md has no log panel, so this is
/// how non-terminal DownloadHandle.LogMessage text reaches the user - the terminal Failed-state
/// case instead persists onto the row via DownloadRowViewModel.LastError). Auto-dismisses after a
/// fixed delay unless <paramref name="autoDismiss"/> is false (used for the update-available
/// toast, which should stay until the user acts on or dismisses it), or immediately via
/// DismissCommand. Optionally carries a single action button (e.g. "Update now").</summary>
public sealed partial class ToastViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);

    private readonly Action<ToastViewModel> _onDismissed;
    private readonly Action? _onAction;
    private readonly DispatcherTimer? _timer;

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _message;

    public string? ActionLabel { get; }

    public bool HasAction => ActionLabel is not null;

    public ToastViewModel(
        string message,
        Action<ToastViewModel> onDismissed,
        string? actionLabel = null,
        Action? onAction = null,
        bool autoDismiss = true)
    {
        _message = message;
        _onDismissed = onDismissed;
        ActionLabel = actionLabel;
        _onAction = onAction;

        if (autoDismiss)
        {
            _timer = new DispatcherTimer { Interval = AutoDismissDelay };
            _timer.Tick += (_, _) => Dismiss();
            _timer.Start();
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer?.Stop();
        _onDismissed(this);
    }

    [RelayCommand(CanExecute = nameof(HasAction))]
    private void InvokeAction()
    {
        _onAction?.Invoke();
        Dismiss();
    }

    public void Dispose() => _timer?.Stop();
}
