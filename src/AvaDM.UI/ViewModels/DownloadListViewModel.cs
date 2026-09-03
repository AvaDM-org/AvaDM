using System.Collections.ObjectModel;
using System.ComponentModel;
using AvaDM.Core;
using AvaDM.UI.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// Downloads list page: the full set of persisted downloads (each optionally backed by a live
/// <see cref="DownloadHandle"/> when active in this process), a text search over the visible
/// subset, and a periodic reconciliation poll that keeps the list in sync with
/// downloads started/finished/removed elsewhere (or, after a restart, downloads this process
/// hasn't touched yet - see <see cref="DownloadRowViewModel"/>'s derived Interrupted status).
///
/// Newly-added downloads (from the Add Download flow) are inserted immediately via
/// <see cref="AddOrUpdateRow"/> rather than waiting on the next poll tick.
/// </summary>
public sealed partial class DownloadListViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadManager _downloadManager;
    private readonly DownloadSettings _settings;
    private readonly Action _navigateToSettings;
    private readonly Func<DownloadDoubleClickAction> _getDoubleClickAction;
    private readonly DispatcherTimer _reconcileTimer;

    /// <summary>Column layout (order, visibility, widths) and the active sort for the downloads
    /// table. The view binds its header bar and rows to this; a sort change re-runs
    /// <see cref="ApplyFilter"/> so <see cref="FilteredDownloads"/> reorders to match.</summary>
    public DownloadColumnsViewModel Columns { get; }

    /// <summary>Debounces <see cref="SearchText"/> so <see cref="ApplyFilter"/> - an O(n) scan
    /// plus O(n) list-diff per call - runs once typing pauses rather than on every keystroke,
    /// which would otherwise stall the UI thread on a long download list.</summary>
    private readonly DispatcherTimer _searchDebounceTimer;
    private bool _reconciling;

    /// <summary>Full row set, independent of the current filter/search - reconciliation and
    /// row-add/remove operate on this; <see cref="FilteredDownloads"/> is derived from it.</summary>
    private readonly List<DownloadRowViewModel> _allRows = [];

    public ObservableCollection<DownloadRowViewModel> FilteredDownloads { get; } = new();

    /// <summary>Fires whenever a row is added, removed, or changes <see cref="DownloadRowViewModel.DisplayStatus"/>
    /// (start, pause, resume, complete, fail, handle attach/detach) - i.e. whenever the set of
    /// "what's currently happening" genuinely changes, not on every progress tick. Used by
    /// <see cref="AvaDM.UI.Services.TrayIconService"/> to rebuild the tray menu's structure only
    /// at meaningful transitions instead of polling on a timer (tried and reverted - see that
    /// class's doc comment) or relying solely on the unreliable <c>NativeMenu.NeedsUpdate</c>
    /// event. Progress *percentage* text is a separate concern, kept live by a continuously-
    /// running timer - see <see cref="AvaDM.UI.Services.TrayIconService.UpdateLiveProgress"/> -
    /// so this event isn't what drives that.</summary>
    public event EventHandler? DownloadsChanged;

    private void RaiseDownloadsChanged() => DownloadsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Subscribes to a newly-added row's status transitions so <see cref="DownloadsChanged"/>
    /// fires for it. Paired with the unsubscribe in <see cref="RemoveRow"/> - every row added
    /// via <see cref="AddOrUpdateRow"/> or <see cref="ReconcileAsync"/> must go through this.</summary>
    private void TrackRow(DownloadRowViewModel row) => row.PropertyChanged += OnRowPropertyChanged;

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadRowViewModel.DisplayStatus))
            RaiseDownloadsChanged();
    }

    /// <summary>Transient toast/snackbar notifications from non-terminal
    /// <see cref="AvaDM.Core.DownloadHandle.LogMessage"/> events across all rows - see
    /// <see cref="ToastViewModel"/>. The terminal Failed-state case is handled separately, by
    /// each row persisting its own LastError instead.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

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

    /// <summary>Non-null while the Cancel confirmation overlay is open; a fresh instance is
    /// created each time a row's Cancel command runs. <see cref="IsCancelConfirmationOpen"/>
    /// drives the overlay's visibility in AXAML.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCancelConfirmationOpen))]
    private CancelConfirmationViewModel? _activeCancelConfirmation;

    public bool IsCancelConfirmationOpen => ActiveCancelConfirmation is not null;

    public DownloadListViewModel(
        DownloadManager downloadManager,
        DownloadSettings settings,
        UiPreferencesRepository uiPreferences,
        Action navigateToSettings,
        Func<DownloadDoubleClickAction> getDoubleClickAction)
    {
        _downloadManager = downloadManager;
        _settings = settings;
        _navigateToSettings = navigateToSettings;
        _getDoubleClickAction = getDoubleClickAction;

        Columns = new DownloadColumnsViewModel(uiPreferences);
        Columns.SortChanged += (_, _) => ApplyFilter();

        _reconcileTimer = new DispatcherTimer { Interval = ReconcileInterval };
        _reconcileTimer.Tick += async (_, _) => await ReconcileAsync();
        _reconcileTimer.Start();

        _searchDebounceTimer = new DispatcherTimer { Interval = SearchDebounceInterval };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilter();
        };

        _ = ReconcileAsync();
    }

    [RelayCommand]
    private void OpenSettings() => _navigateToSettings();

    /// <summary>Opens the Add Download overlay with a fresh view model wired to close itself
    /// (submitted or cancelled) via the two callbacks below.</summary>
    [RelayCommand]
    private void AddDownload() =>
        ActiveAddDownload = new AddDownloadViewModel(_downloadManager, _settings, OnAddDownloadSubmitted, OnAddDownloadCancelled);

    private void OnAddDownloadSubmitted(DownloadRecord record, DownloadHandle handle)
    {
        AddOrUpdateRow(record, handle);
        ActiveAddDownload = null;
    }

    private void OnAddDownloadCancelled() => ActiveAddDownload = null;

    /// <summary>Public so callers outside this view model - currently just
    /// <see cref="App.OnFrameworkInitializationCompleted"/>, notifying that a second launch was
    /// redirected here - can post a toast without duplicating <see cref="ToastViewModel"/>'s
    /// wiring.</summary>
    public void ShowToast(string message) => Toasts.Add(new ToastViewModel(message, RemoveToast));

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

    private void RequestCancel(DownloadRowViewModel row) =>
        ActiveCancelConfirmation = new CancelConfirmationViewModel(
            _downloadManager,
            row,
            OnCancelConfirmed,
            OnCancelDismissed);

    private void OnCancelConfirmed() => ActiveCancelConfirmation = null;

    private void OnCancelDismissed() => ActiveCancelConfirmation = null;

    partial void OnSearchTextChanged(string value)
    {
        _ = value;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>Inserts a freshly-started download's row immediately (Add Download flow), or
    /// refreshes an existing row in place if one with the same id is already present.</summary>
    public DownloadRowViewModel AddOrUpdateRow(DownloadRecord record, DownloadHandle? handle)
    {
        var existing = _allRows.FirstOrDefault(r => r.Id == record.Id);
        if (existing is not null)
        {
            if (handle is not null)
            {
                // AddDownloadAsync's Resume/Overwrite paths hand back a brand-new handle for this
                // same id (see DownloadManager.AddDownloadAsync's ResetForRestartAsync comment) -
                // any handle this row was already wired to is now stale (its download finished,
                // failed, or was cancelled), so swap it out rather than leaving the row frozen on
                // the old handle's terminal state while the new download runs invisibly.
                if (existing.HasActiveHandle)
                    existing.Detach();
                existing.AttachHandle(handle);
            }
            else
            {
                existing.UpdateFromRecord(record);
            }

            return existing;
        }

        var row = new DownloadRowViewModel(
            _downloadManager, record, handle, RequestRemove, RequestCancel, ShowToast, _getDoubleClickAction);
        _allRows.Add(row);
        TrackRow(row);
        ApplyFilter();
        RaiseDownloadsChanged();
        return row;
    }

    /// <summary>Snapshot of rows that are actively downloading right now (Running only),
    /// independent of the UI's current filter/search - used by <see cref="AvaDM.UI.Services.TrayIconService"/>
    /// to populate the tray menu's per-download entries. Deliberately narrower than the "Active"
    /// toolbar filter (which also includes Paused/Pending/Interrupted): the tray menu is meant to
    /// be a quick glance at in-progress transfers, not a full queue view.</summary>
    public IReadOnlyList<DownloadRowViewModel> GetDownloadingDownloads() =>
        _allRows.Where(r => r.DisplayStatus == DownloadDisplayStatus.Running).ToList();

    /// <summary>Drops a row from the list (Remove Download flow) and unhooks its handle events.</summary>
    public void RemoveRow(Guid id)
    {
        var row = _allRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return;

        row.PropertyChanged -= OnRowPropertyChanged;
        row.Detach();
        _allRows.Remove(row);
        FilteredDownloads.Remove(row);
        RaiseDownloadsChanged();
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
                    var newRow = new DownloadRowViewModel(
                        _downloadManager,
                        record,
                        _downloadManager.GetActiveHandle(record.Id),
                        RequestRemove,
                        RequestCancel,
                        ShowToast,
                        _getDoubleClickAction);
                    _allRows.Add(newRow);
                    TrackRow(newRow);
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

            // removed.Count > 0 already raised once per row via RemoveRow above.
            if (changed)
                RaiseDownloadsChanged();
        }
        finally
        {
            _reconciling = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var matches = _allRows.Where(r => MatchesSearch(r, search)).ToList();
        SortMatches(matches);

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

    /// <summary>Orders <paramref name="rows"/> by the active sort column + direction (see
    /// <see cref="Columns"/>). File name then id are the tie-breakers so the order is stable
    /// across ticks even when the primary key is equal (e.g. two pending downloads, both 0%).</summary>
    private void SortMatches(List<DownloadRowViewModel> rows)
    {
        Comparison<DownloadRowViewModel> primary = Columns.SortColumnId switch
        {
            DownloadColumnId.Name => (a, b) => NameCompare(a, b),
            DownloadColumnId.Type => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            DownloadColumnId.Size => (a, b) => a.TotalBytes.CompareTo(b.TotalBytes),
            DownloadColumnId.Created => (a, b) => a.CreatedAt.CompareTo(b.CreatedAt),
            DownloadColumnId.Speed => (a, b) => Nullable.Compare(a.SpeedBytesPerSecond, b.SpeedBytesPerSecond),
            DownloadColumnId.ProgressPercent => (a, b) => a.ProgressPercent.CompareTo(b.ProgressPercent),
            DownloadColumnId.ProgressSize => (a, b) => a.BytesDownloaded.CompareTo(b.BytesDownloaded),
            _ => (a, b) => a.CreatedAt.CompareTo(b.CreatedAt),
        };

        var sign = Columns.SortAscending ? 1 : -1;
        rows.Sort((a, b) =>
        {
            var result = primary(a, b);
            if (result != 0)
                return sign * result;

            var tie = NameCompare(a, b);
            return tie != 0 ? tie : a.Id.CompareTo(b.Id);
        });
    }

    private static int NameCompare(DownloadRowViewModel a, DownloadRowViewModel b) =>
        string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSearch(DownloadRowViewModel row, string search) =>
        string.IsNullOrEmpty(search)
        || row.FileName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.SourceUrl.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _reconcileTimer.Stop();
        _searchDebounceTimer.Stop();
    }
}
