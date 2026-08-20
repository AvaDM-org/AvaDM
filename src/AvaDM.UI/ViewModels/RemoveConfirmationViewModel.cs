using AvaDM.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Overlay-hosted confirmation for removing a download from the list, with an optional
/// deletion of the downloaded file from disk.</summary>
public sealed partial class RemoveConfirmationViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly Guid _downloadId;

    public string FileName { get; }
    public bool IsActive { get; }

    [ObservableProperty]
    private bool _deleteFile;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    private readonly Action _onRemoved;
    private readonly Action _onCancelled;

    public RemoveConfirmationViewModel(
        DownloadManager downloadManager,
        DownloadRowViewModel row,
        Action onRemoved,
        Action onCancelled)
    {
        _downloadManager = downloadManager;
        _downloadId = row.Id;
        FileName = row.FileName;
        IsActive = row.IsActive;
        _onRemoved = onRemoved;
        _onCancelled = onCancelled;
    }

    public string WarningText => IsActive
        ? "This download is still in progress. Removing it will cancel the download. This cannot be undone."
        : "This will remove the download from your list. This cannot be undone.";

    [RelayCommand]
    private async Task Confirm()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var (success, error) = await _downloadManager.RemoveDownloadAsync(_downloadId, DeleteFile);
            if (success)
                _onRemoved();
            else
                ErrorMessage = error ?? "Failed to remove the download.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onCancelled();
}
