using AvaDM.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Overlay-hosted confirmation for cancelling an in-progress download: cancelling always
/// deletes the download's <c>.avadm</c> progress file, so this warns the user before doing so
/// rather than deleting silently.</summary>
public sealed partial class CancelConfirmationViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly Guid _downloadId;

    public string FileName { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    private readonly Action _onCancelled;
    private readonly Action _onDismissed;

    public CancelConfirmationViewModel(
        DownloadManager downloadManager,
        DownloadRowViewModel row,
        Action onCancelled,
        Action onDismissed)
    {
        _downloadManager = downloadManager;
        _downloadId = row.Id;
        FileName = row.FileName;
        _onCancelled = onCancelled;
        _onDismissed = onDismissed;
    }

    [RelayCommand]
    private async Task Confirm()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var (success, error) = await _downloadManager.CancelDownloadAsync(_downloadId);
            if (success)
                _onCancelled();
            else
                ErrorMessage = error ?? "Failed to cancel the download.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => _onDismissed();
}
