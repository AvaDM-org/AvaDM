using System.Collections.ObjectModel;
using AvaDM.Core;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>Status filter for the downloads list toolbar.</summary>
public enum DownloadListStatusFilter
{
    All,
    Active,
    Completed,
    Failed,
}

/// <summary>
/// Downloads list page: the full set of persisted downloads (each optionally backed by a live
/// <see cref="DownloadHandle"/> when active in this process), a status filter + text search over
/// the visible subset, and a periodic reconciliation poll that keeps the list in sync with
/// downloads started/finished/removed elsewhere (or, after a restart, downloads this process
/// hasn't touched yet - see <see cref="DownloadRowViewModel"/>'s derived Interrupted status).
///
/// Newly-added downloads (from the Add Download flow) are inserted immediately via
/// <see cref="AddOrUpdateRow"/> rather than waiting on the next poll tick.
/// </summary>
public sealed partial class DownloadListViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);

    private readonly DownloadManager _downloadManager;
    private readonly Action _navigateToSettings;
    private readonly DispatcherTimer _reconcileTimer;
    private bool _reconciling;

    /// <summary>Full row set, independent of the current filter/search - reconciliation and
    /// row-add/remove operate on this; <see cref="FilteredDownloads"/> is derived from it.</summary>
    private readonly List<DownloadRowViewModel> _allRows = [];

    public ObservableCollection<DownloadRowViewModel> FilteredDownloads { get; } = new();

    /// <summary>Transient toast/snackbar notifications from non-terminal
    /// <see cref="AvaDM.Core.DownloadHandle.LogMessage"/> events across all rows - see
    /// <see cref="ToastViewModel"/>. The terminal Failed-state case is handled separately, by
    /// each row persisting its own LastError instead.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    [NotifyPropertyChangedFor(nameof(IsFilterCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFilterFailed))]
    private DownloadListStatusFilter _statusFilter = DownloadListStatusFilter.All;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Non-null while the Add Download overlay is open; a fresh instance is created
    /// each time <see cref="AddDownload"/> runs, so its state never needs resetting between
    /// opens. <see cref="IsAddDownloadOpen"/> drives the overlay's visibility in AXAML.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddDownloadOpen))]
    private AddDownloadViewModel? _activeAddDownload;

    public bool IsAddDownloadOpen => ActiveAddDownload is not null;

    /// <summary>Non-null while the Remove confirmation overlay is open; a fresh instance is
    /// created each time a row's Remove command runs. <see cref="IsRemoveConfirmationOpen"/>
    /// drives the overlay's visibility in AXAML.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveConfirmationOpen))]
    private RemoveConfirmationViewModel? _activeRemoveConfirmation;

    public bool IsRemoveConfirmationOpen => ActiveRemoveConfirmation is not null;

    /// <summary>Selected-state flags for the toolbar's filter tabs, one per
    /// <see cref="DownloadListStatusFilter"/> value. AXAML has no direct way to bind a style
    /// class to a view-model bool, so each tab is two overlaid buttons (selected/unselected
    /// styling) toggled by these - the same pattern already used for the row expand/collapse
    /// glyph in <c>DownloadRowView.axaml</c>.</summary>
    public bool IsFilterAll => StatusFilter == DownloadListStatusFilter.All;

    public bool IsFilterActive => StatusFilter == DownloadListStatusFilter.Active;

    public bool IsFilterCompleted => StatusFilter == DownloadListStatusFilter.Completed;

    public bool IsFilterFailed => StatusFilter == DownloadListStatusFilter.Failed;

    public DownloadListViewModel(DownloadManager downloadManager, Action navigateToSettings)
    {
        _downloadManager = downloadManager;
        _navigateToSettings = navigateToSettings;

        _reconcileTimer = new DispatcherTimer { Interval = ReconcileInterval };
        _reconcileTimer.Tick += async (_, _) => await ReconcileAsync();
        _reconcileTimer.Start();

        _ = ReconcileAsync();
    }

    [RelayCommand]
    private void OpenSettings() => _navigateToSettings();

    [RelayCommand]
    private void SetStatusFilter(DownloadListStatusFilter filter) => StatusFilter = filter;

    /// <summary>Opens the Add Download overlay with a fresh view model wired to close itself
    /// (submitted or cancelled) via the two callbacks below.</summary>
    [RelayCommand]
    private void AddDownload() =>
        ActiveAddDownload = new AddDownloadViewModel(_downloadManager, OnAddDownloadSubmitted, OnAddDownloadCancelled);

    private void OnAddDownloadSubmitted(DownloadRecord record, DownloadHandle handle)
    {
        AddOrUpdateRow(record, handle);
        ActiveAddDownload = null;
    }

    private void OnAddDownloadCancelled() => ActiveAddDownload = null;

    private void ShowToast(string message) => Toasts.Add(new ToastViewModel(message, RemoveToast));

    private void RemoveToast(ToastViewModel toast)
    {
        toast.Dispose();
        Toasts.Remove(toast);
    }

    private void RequestRemove(DownloadRowViewModel row) =>
        ActiveRemoveConfirmation = new RemoveConfirmationViewModel(
            _downloadManager,
            row,
            () => OnRemoveConfirmed(row),
            OnRemoveCancelled);

    private void OnRemoveConfirmed(DownloadRowViewModel row)
    {
        RemoveRow(row.Id);
        ActiveRemoveConfirmation = null;
    }

    private void OnRemoveCancelled() => ActiveRemoveConfirmation = null;

    partial void OnStatusFilterChanged(DownloadListStatusFilter value)
    {
        _ = value;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        ApplyFilter();
    }

    /// <summary>Inserts a freshly-started download's row immediately (Add Download flow), or
    /// refreshes an existing row in place if one with the same id is already present.</summary>
    public DownloadRowViewModel AddOrUpdateRow(DownloadRecord record, DownloadHandle? handle)
    {
        var existing = _allRows.FirstOrDefault(r => r.Id == record.Id);
        if (existing is not null)
        {
            if (handle is not null && !existing.HasActiveHandle)
                existing.AttachHandle(handle);
            else
                existing.UpdateFromRecord(record);

            return existing;
        }

        var row = new DownloadRowViewModel(_downloadManager, record, handle, RequestRemove, ShowToast);
        _allRows.Add(row);
        ApplyFilter();
        return row;
    }

    /// <summary>Drops a row from the list (Remove Download flow) and unhooks its handle events.</summary>
    public void RemoveRow(Guid id)
    {
        var row = _allRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return;

        row.Detach();
        _allRows.Remove(row);
        FilteredDownloads.Remove(row);
    }

    /// <summary>Merges a fresh repository snapshot into <see cref="_allRows"/>: adds rows for
    /// records not yet seen, refreshes handle-less rows from their record, drops rows whose
    /// record no longer exists. Rows already driven by a live handle are left to their own
    /// events rather than overwritten here - the record snapshot lags behind those.</summary>
    private async Task ReconcileAsync()
    {
        if (_reconciling)
            return;

        _reconciling = true;
        try
        {
            var records = await _downloadManager.GetAllDownloadsAsync();
            var recordIds = records.Select(r => r.Id).ToHashSet();

            var removed = _allRows.Where(r => !recordIds.Contains(r.Id)).ToList();
            foreach (var row in removed)
                RemoveRow(row.Id);

            var changed = false;
            foreach (var record in records)
            {
                var existing = _allRows.FirstOrDefault(r => r.Id == record.Id);
                if (existing is null)
                {
                    _allRows.Add(new DownloadRowViewModel(
                        _downloadManager,
                        record,
                        _downloadManager.GetActiveHandle(record.Id),
                        RequestRemove,
                        ShowToast));
                    changed = true;
                }
                else if (!existing.HasActiveHandle)
                {
                    var handle = _downloadManager.GetActiveHandle(record.Id);
                    if (handle is not null)
                        existing.AttachHandle(handle);
                    else
                        existing.UpdateFromRecord(record);
                }
            }

            if (changed || removed.Count > 0)
                ApplyFilter();
        }
        finally
        {
            _reconciling = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var matches = _allRows.Where(MatchesStatusFilter).Where(r => MatchesSearch(r, search)).ToList();

        for (var i = FilteredDownloads.Count - 1; i >= 0; i--)
        {
            if (!matches.Contains(FilteredDownloads[i]))
                FilteredDownloads.RemoveAt(i);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            if (i >= FilteredDownloads.Count || FilteredDownloads[i] != matches[i])
            {
                if (FilteredDownloads.Contains(matches[i]))
                    FilteredDownloads.Move(FilteredDownloads.IndexOf(matches[i]), i);
                else
                    FilteredDownloads.Insert(i, matches[i]);
            }
        }
    }

    private bool MatchesStatusFilter(DownloadRowViewModel row) => StatusFilter switch
    {
        DownloadListStatusFilter.All => true,
        DownloadListStatusFilter.Active => row.DisplayStatus is DownloadDisplayStatus.Running
            or DownloadDisplayStatus.Paused or DownloadDisplayStatus.Pending or DownloadDisplayStatus.Interrupted,
        DownloadListStatusFilter.Completed => row.DisplayStatus == DownloadDisplayStatus.Completed,
        DownloadListStatusFilter.Failed => row.DisplayStatus == DownloadDisplayStatus.Failed,
        _ => true,
    };

    private static bool MatchesSearch(DownloadRowViewModel row, string search) =>
        string.IsNullOrEmpty(search)
        || row.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.SourceUrl.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _reconcileTimer.Stop();
}
