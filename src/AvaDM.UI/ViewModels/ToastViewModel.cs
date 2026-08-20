using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>One transient toast/snackbar notification (design.md has no log panel, so this is
/// how non-terminal DownloadHandle.LogMessage text reaches the user - the terminal Failed-state
/// case instead persists onto the row via DownloadRowViewModel.LastError). Auto-dismisses after a
/// fixed delay, or immediately via DismissCommand.</summary>
public sealed partial class ToastViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(5);

    private readonly Action<ToastViewModel> _onDismissed;
    private readonly DispatcherTimer _timer;

    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _message;

    public ToastViewModel(string message, Action<ToastViewModel> onDismissed)
    {
        _message = message;
        _onDismissed = onDismissed;

        _timer = new DispatcherTimer { Interval = AutoDismissDelay };
        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _timer.Stop();
        _onDismissed(this);
    }

    public void Dispose() => _timer.Stop();
}
