using AvaDM.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Result of a bulk remove: which downloads were actually removed, and how many (and why)
/// weren't.</summary>
public sealed record BulkRemoveOutcome(IReadOnlyList<Guid> RemovedIds, int FailureCount, string? FirstError);

/// <summary>
/// Overlay confirmation for removing several downloads at once (issue #19) - raised by the
/// toolbar trash icon or the Delete key. Lists the file names being removed and offers a single
/// "also delete the files from disk" toggle that applies to all of them, then loops the same
/// per-download removal (<see cref="DownloadManager.RemoveDownloadAsync"/>) the single-row flow
/// uses - so an in-progress download in the set is cancelled first, exactly as it would be on its
/// own.
/// </summary>
public sealed partial class BulkRemoveConfirmationViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly IReadOnlyList<DownloadRowViewModel> _rows;
    private readonly Action<BulkRemoveOutcome> _onOutcome;
    private readonly Action _onCancelled;

    public IReadOnlyList<string> FileNames { get; }

    public int Count => _rows.Count;

    public string Title => $"Remove {Count} download{(Count == 1 ? string.Empty : "s")}";

    public bool AnyActive { get; }

    public string WarningText => AnyActive
        ? "Some of these downloads are still in progress and will be cancelled. This cannot be undone."
        : "This removes the selected downloads from your list. This cannot be undone.";

    [ObservableProperty]
    private bool _deleteFile;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public BulkRemoveConfirmationViewModel(
        DownloadManager downloadManager,
        IReadOnlyList<DownloadRowViewModel> rows,
        Action<BulkRemoveOutcome> onOutcome,
        Action onCancelled)
    {
        _downloadManager = downloadManager;
        _rows = rows;
        _onOutcome = onOutcome;
        _onCancelled = onCancelled;
        FileNames = rows.Select(r => r.FileName).ToList();
        AnyActive = rows.Any(r => r.IsActive);
    }

    [RelayCommand]
    private async Task Confirm()
    {
        IsBusy = true;
        ErrorMessage = null;

        var removed = new List<Guid>();
        var failures = 0;
        string? firstError = null;

        foreach (var row in _rows)
        {
            var (success, error) = await _downloadManager.RemoveDownloadAsync(row.Id, DeleteFile);
            if (success)
            {
                removed.Add(row.Id);
            }
            else
            {
                failures++;
                firstError ??= error;
            }
        }

        _onOutcome(new BulkRemoveOutcome(removed, failures, firstError));

        if (failures > 0)
        {
            ErrorMessage = $"{failures} download{(failures == 1 ? string.Empty : "s")} could not be removed" +
                           (firstError is null ? "." : $": {firstError}");
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onCancelled();
}
