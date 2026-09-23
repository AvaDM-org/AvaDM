using System.Collections.ObjectModel;
using System.IO;
using AvaDM.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>One link in the bulk-import list: a checkbox plus, after a failed start, why.</summary>
public sealed partial class BulkImportItemViewModel : ObservableObject
{
    public Uri Uri { get; }

    public string Url => Uri.AbsoluteUri;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public BulkImportItemViewModel(Uri uri) => Uri = uri;
}

/// <summary>
/// Overlay for importing many links at once (issue #32), opened from the advanced-add dialog's
/// "import from clipboard / file" buttons. Lists the parsed links with a checkbox each, plus one
/// editable destination folder shared by the batch. Starting adds every selected link through
/// <see cref="DownloadManager.AddDownloadAsync"/> - the same call the quick-add box makes - so the
/// concurrency limit decides which start now and which queue.
///
/// Links that were added leave the list; ones that couldn't be (typically an existing
/// <c>(URL, destination)</c> download) stay with an error so the user can untick them or change the
/// folder and try again. The dialog closes once nothing is left to add.
/// </summary>
public sealed partial class BulkImportViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;
    private readonly Action<DownloadRecord, DownloadHandle?> _onAdded;
    private readonly Action _onClosed;

    public ObservableCollection<BulkImportItemViewModel> Items { get; }

    [ObservableProperty]
    private string _saveDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCount))]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _selectionRevision;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public int SelectedCount => Items.Count(i => i.IsSelected);

    public string SelectionSummary => $"{SelectedCount} of {Items.Count} selected";

    public string StartButtonText => SelectedCount == 1 ? "Start 1 download" : $"Start {SelectedCount} downloads";

    public BulkImportViewModel(
        DownloadManager downloadManager,
        IEnumerable<Uri> links,
        string saveDirectory,
        Action<DownloadRecord, DownloadHandle?> onAdded,
        Action onClosed)
    {
        _downloadManager = downloadManager;
        _onAdded = onAdded;
        _onClosed = onClosed;
        _saveDirectory = saveDirectory;

        Items = new ObservableCollection<BulkImportItemViewModel>(links.Select(l => new BulkImportItemViewModel(l)));
        foreach (var item in Items)
            item.PropertyChanged += OnItemPropertyChanged;
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BulkImportItemViewModel.IsSelected))
            SelectionRevision++;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        ErrorMessage = null;

        var directory = SaveDirectory.Trim();
        if (directory.Length == 0)
        {
            ErrorMessage = "Choose a folder to save the downloads to.";
            return;
        }

        if (directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            ErrorMessage = "The download folder contains characters that aren't allowed in a path.";
            return;
        }

        IsBusy = true;
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = 0;

            foreach (var item in Items.Where(i => i.IsSelected).ToList())
            {
                item.Error = null;
                var destination = Path.Combine(directory, UniqueFileName(item.Uri, usedNames));

                var result = await _downloadManager.AddDownloadAsync(item.Uri, destination);
                if (!result.Success)
                {
                    failures++;
                    item.Error = result.Conflict?.HasConflict == true
                        ? "Already in your downloads for this folder."
                        : result.Error ?? "Could not start the download.";
                    continue;
                }

                var record = await _downloadManager.GetDownloadAsync(result.Id!.Value);
                if (record is not null)
                    _onAdded(record, result.Handle);

                item.PropertyChanged -= OnItemPropertyChanged;
                Items.Remove(item);
            }

            SelectionRevision++;

            if (failures == 0 && Items.Count == 0)
                _onClosed();
            else if (failures > 0)
                ErrorMessage = $"{failures} link{(failures == 1 ? " was" : "s were")} not added. Untick {(failures == 1 ? "it" : "them")} or change the folder and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClosed();

    private bool CanStart() => !IsBusy && SelectedCount > 0;

    /// <summary>The name <see cref="Downloader.SuggestFileName"/> gives the link, with a
    /// <c>" (2)"</c>-style suffix when an earlier link in this batch already took it - two links
    /// that both end in <c>download.zip</c> would otherwise write to the same file.</summary>
    private static string UniqueFileName(Uri uri, HashSet<string> usedNames)
    {
        var name = Downloader.SuggestFileName(uri);
        if (usedNames.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }
}
