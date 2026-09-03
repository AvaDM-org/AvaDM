using System.Collections.ObjectModel;
using System.ComponentModel;
using AvaDM.Core;
using AvaDM.UI.Converters;
using AvaDM.UI.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>What double-clicking a completed row in the downloads list does, per the Settings
/// page's "Downloaded item double-click" choice (see <see cref="SettingsViewModel"/>). The
/// row's separate folder-icon button always opens the containing folder regardless of this
/// setting - this enum only governs the double-click gesture.</summary>
public enum DownloadDoubleClickAction
{
    OpenFile,
    OpenContainingFolder,
}

/// <summary>
/// Display-only status that adds the derived "Interrupted" case on top of the real
/// <see cref="DownloadState"/>: a persisted record whose state still looks active
/// (Pending/Running/Paused) but has no live <see cref="DownloadHandle"/> in this process - e.g.
/// after an app restart - shows as Interrupted rather than a stale "Running" nothing is actually
/// driving. Not a new <see cref="DownloadState"/> itself, per docs/ui-implementation-plan.md.
/// </summary>
public enum DownloadDisplayStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

/// <summary>
/// One row in the downloads list: a persisted download identity (id/URL/destination) plus,
/// while this download is active in the current process, the live <see cref="DownloadHandle"/>
/// driving its progress and chunk events. A row can exist without a handle (persisted-only, not
/// yet resumed this process - see <see cref="DownloadListViewModel"/>'s reconciliation) or gain
/// one later via <see cref="AttachHandle"/>.
///
/// Every handle event handler marshals back to the UI thread via
/// <see cref="Dispatcher.UIThread"/>: <see cref="DownloadHandle"/> raises its events from
/// background chunk tasks, and Avalonia bindings must only be touched from the UI thread.
/// </summary>
public sealed partial class DownloadRowViewModel : ViewModelBase
{
    /// <summary>Trailing-cell properties (one per column type) whose value comes from this row.
    /// A change to any of them refreshes <see cref="Cells"/>.</summary>
    private static readonly HashSet<string> CellSourceProperties =
    [
        nameof(Extension), nameof(SizeText), nameof(CreatedText), nameof(SpeedText),
        nameof(ProgressPercentText), nameof(BytesText), nameof(RunningEtaText),
    ];

    private readonly DownloadManager _downloadManager;
    private readonly DownloadColumnsViewModel _columns;
    private readonly Action<DownloadRowViewModel> _onRemoveRequested;
    private readonly Action<DownloadRowViewModel> _onContextRemoveRequested;
    private readonly Action<DownloadRowViewModel> _onCancelRequested;
    private readonly Action<string> _onLogMessage;
    private readonly Func<DownloadDoubleClickAction> _getDoubleClickAction;
    private DownloadHandle? _handle;

    public Guid Id { get; }

    /// <summary>When this download was first added, straight from its persisted record. Immutable
    /// for the row's lifetime; drives the "Created" column and its sort.</summary>
    public DateTime CreatedAt { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Extension))]
    private string _fileName;

    [ObservableProperty]
    private string _destinationPath;

    [ObservableProperty]
    private string _sourceUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusChipClass))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    [NotifyPropertyChangedFor(nameof(RunningEtaText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    [NotifyPropertyChangedFor(nameof(ShowProgressBar))]
    [NotifyPropertyChangedFor(nameof(CanOpenDownload))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDownloadCommand))]
    private DownloadState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusChipClass))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    [NotifyPropertyChangedFor(nameof(RunningEtaText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    [NotifyPropertyChangedFor(nameof(ShowProgressBar))]
    [NotifyPropertyChangedFor(nameof(CanOpenDownload))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDownloadCommand))]
    private bool _hasActiveHandle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    [NotifyPropertyChangedFor(nameof(RunningEtaText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    private long _bytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    [NotifyPropertyChangedFor(nameof(RunningEtaText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercentText))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    [NotifyPropertyChangedFor(nameof(RunningEtaText))]
    private double? _speedBytesPerSecond;

    /// <summary>Last error text for a Failed row, shown as a red line under the name/path - there
    /// is no log panel, so this is the only place the failure reason surfaces.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastError))]
    private string? _lastError;

    /// <summary>Whether <see cref="LastError"/> has text to show. A plain computed bool rather
    /// than an AXAML string-to-visibility converter, matching this codebase's
    /// converter-free-view-model convention (see <c>Converters/FormatHelpers.cs</c>).</summary>
    public bool HasLastError => !string.IsNullOrEmpty(LastError);

    /// <summary>Speed-limit for this download, in bytes/sec (<c>null</c> = unlimited). Edited from
    /// the Speed column cell and applied to the live handle immediately on change (see
    /// <see cref="OnSpeedLimitBytesPerSecondChanged"/>), since <see cref="DownloadHandle.SetSpeedLimit"/>
    /// is cheap to call repeatedly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitDisplayText))]
    private long? _speedLimitBytesPerSecond;

    /// <summary>Secondary line under the current speed in the Speed cell: the active limit, or
    /// "no limit".</summary>
    public string SpeedLimitDisplayText => SpeedLimitBytesPerSecond is { } bytesPerSecond
        ? $"limit {FormatHelpers.FormatBytes(bytesPerSecond)}/s"
        : "no limit";

    /// <summary>Per-connection snapshots feeding the name cell's segmented progress bar.</summary>
    public ObservableCollection<ChunkRowViewModel> Chunks { get; } = new();

    /// <summary>Trailing-column cells for this row, in the current column order. Rebuilt when the
    /// shared column layout changes; each cell's text is refreshed when this row's data changes.</summary>
    public ObservableCollection<DownloadCellViewModel> Cells { get; } = new();

    public DownloadRowViewModel(
        DownloadManager downloadManager,
        DownloadColumnsViewModel columns,
        DownloadRecord record,
        DownloadHandle? handle,
        Action<DownloadRowViewModel> onRemoveRequested,
        Action<DownloadRowViewModel> onContextRemoveRequested,
        Action<DownloadRowViewModel> onCancelRequested,
        Action<string> onLogMessage,
        Func<DownloadDoubleClickAction> getDoubleClickAction)
    {
        _downloadManager = downloadManager;
        _columns = columns;
        _onRemoveRequested = onRemoveRequested;
        _onContextRemoveRequested = onContextRemoveRequested;
        _onCancelRequested = onCancelRequested;
        _onLogMessage = onLogMessage;
        _getDoubleClickAction = getDoubleClickAction;
        Id = record.Id;
        CreatedAt = record.CreatedAt;
        _fileName = Path.GetFileName(record.DestinationPath);
        _destinationPath = record.DestinationPath;
        _sourceUrl = record.Uri;
        _state = record.State;
        _bytesDownloaded = record.BytesDownloaded;
        _totalBytes = record.TotalBytes;

        if (handle is not null)
            AttachHandle(handle);

        RebuildCells();
        _columns.LayoutChanged += OnColumnLayoutChanged;
        PropertyChanged += OnSelfPropertyChanged;
    }

    /// <summary>Detaches this row's shared-state subscriptions. Called by
    /// <see cref="DownloadListViewModel"/> when the row leaves the list, alongside
    /// <see cref="Detach"/>.</summary>
    public void Release()
    {
        _columns.LayoutChanged -= OnColumnLayoutChanged;
        PropertyChanged -= OnSelfPropertyChanged;
    }

    private void OnColumnLayoutChanged(object? sender, EventArgs e) => RebuildCells();

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && CellSourceProperties.Contains(e.PropertyName))
        {
            foreach (var cell in Cells)
                cell.Refresh();
        }
    }

    private void RebuildCells()
    {
        Cells.Clear();
        foreach (var column in _columns.VisibleTrailingColumns)
            Cells.Add(new DownloadCellViewModel(column, this));
    }

    public double ProgressPercent => TotalBytes > 0 ? BytesDownloaded * 100.0 / TotalBytes : 0.0;

    /// <summary>File extension without the leading dot (e.g. "mkv"), or empty when the name has
    /// none. Backs the "Type" column.</summary>
    public string Extension => Path.GetExtension(FileName).TrimStart('.');

    /// <summary>Total size for the "Size" column - an em dash until the size is known.</summary>
    public string SizeText => TotalBytes > 0 ? FormatHelpers.FormatBytes(TotalBytes) : "—";

    /// <summary>Add date/time for the "Created" column. Local time, fixed
    /// <c>yyyy-MM-dd HH:mm</c> format (the app runs invariant - see <c>InvariantGlobalization</c>).</summary>
    public string CreatedText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Percent text for the "Progress %" column - an em dash while the size is unknown.</summary>
    public string ProgressPercentText => IsSizeUnknown ? "—" : $"{ProgressPercent:N0}%";

    /// <summary>Secondary line under the two progress columns: time remaining, but only while a
    /// running download has enough information to estimate it. Empty otherwise.</summary>
    public string RunningEtaText =>
        HasActiveHandle && State == DownloadState.Running && TotalBytes > 0 && SpeedBytesPerSecond is > 0
            ? FormatHelpers.FormatEta(TotalBytes - BytesDownloaded, SpeedBytesPerSecond)
            : string.Empty;

    /// <summary>Whether the name cell shows the inline aggregate progress bar - only for a
    /// download this process is actively running, has paused, or is starting. Interrupted and
    /// terminal rows show just the name + path (row height varies, per the #19 design decision).</summary>
    public bool ShowProgressBar =>
        HasActiveHandle && State is DownloadState.Running or DownloadState.Paused or DownloadState.Pending;

    /// <summary>True while a download is actively running but its total size isn't known yet -
    /// a server that didn't report <c>Content-Length</c> (see <c>Downloader</c>'s unknown-size
    /// fallback). <see cref="TotalBytes"/> is backfilled with the real size once the download
    /// finishes, so this only applies mid-run. Drives the row's progress bar into indeterminate
    /// mode instead of sitting stuck at 0%.</summary>
    public bool IsSizeUnknown => HasActiveHandle && State == DownloadState.Running && TotalBytes <= 0;

    public string BytesText => TotalBytes > 0
        ? $"{FormatHelpers.FormatBytes(BytesDownloaded)} / {FormatHelpers.FormatBytes(TotalBytes)}"
        : $"{FormatHelpers.FormatBytes(BytesDownloaded)} / ???";

    public string SpeedText => HasActiveHandle && State == DownloadState.Running
        ? FormatHelpers.FormatSpeed(SpeedBytesPerSecond)
        : "-";

    public string EtaText => HasActiveHandle && State == DownloadState.Running && TotalBytes > 0
        ? FormatHelpers.FormatEta(TotalBytes - BytesDownloaded, SpeedBytesPerSecond)
        : "-";

    public DownloadDisplayStatus DisplayStatus =>
        !HasActiveHandle && State is DownloadState.Pending or DownloadState.Running or DownloadState.Paused
            ? DownloadDisplayStatus.Interrupted
            : State switch
            {
                DownloadState.Pending => DownloadDisplayStatus.Pending,
                DownloadState.Running => DownloadDisplayStatus.Running,
                DownloadState.Paused => DownloadDisplayStatus.Paused,
                DownloadState.Completed => DownloadDisplayStatus.Completed,
                DownloadState.Failed => DownloadDisplayStatus.Failed,
                DownloadState.Cancelled => DownloadDisplayStatus.Cancelled,
                _ => DownloadDisplayStatus.Pending,
            };

    public string StatusText => DisplayStatus.ToString();

    /// <summary>Style-class name for the reusable status chip control, matching the semantic
    /// mapping documented in <c>Styles/StatusChip.axaml</c>: completed→success,
    /// running→info, paused→warning, failed→danger, pending/cancelled/interrupted→neutral.</summary>
    public string StatusChipClass => DisplayStatus switch
    {
        DownloadDisplayStatus.Completed => "success",
        DownloadDisplayStatus.Running => "info",
        DownloadDisplayStatus.Paused => "warning",
        DownloadDisplayStatus.Failed => "danger",
        _ => "neutral",
    };

    public bool IsActive => HasActiveHandle && State is DownloadState.Running or DownloadState.Paused or DownloadState.Pending;

    public bool CanPause => HasActiveHandle && State == DownloadState.Running;

    public bool CanResume => (HasActiveHandle && State == DownloadState.Paused)
        || DisplayStatus is DownloadDisplayStatus.Interrupted or DownloadDisplayStatus.Failed;

    public bool CanCancel => HasActiveHandle && State is DownloadState.Running or DownloadState.Paused or DownloadState.Pending;

    /// <summary>Whether double-clicking this row (or its name) opens anything at all - only
    /// once the download has actually finished and the final file exists at
    /// <see cref="DestinationPath"/>. The folder-icon button below is not gated by this: it
    /// always tries to open the containing folder regardless of status.</summary>
    public bool CanOpenDownload => DisplayStatus == DownloadDisplayStatus.Completed;

    /// <summary>Wires this row to a live handle - either at construction (freshly-started or
    /// already-active-in-process download) or later, when reconciliation discovers this
    /// process now owns a handle for a previously handle-less (persisted-only) row.</summary>
    public void AttachHandle(DownloadHandle handle)
    {
        _handle = handle;
        HasActiveHandle = true;
        State = handle.State;
        SpeedLimitBytesPerSecond = handle.SpeedLimitBytesPerSecond;

        // A freshly started handle is "hot" (see DownloadHandle.Start) - it starts running in the
        // background and is returned here before its own HEAD request/.avadm-footer read have
        // populated TotalBytes/Chunks, which can easily still be behind whatever this call is
        // racing against (e.g. AddDownloadAsync's own SQLite write). Applying that not-yet-seeded
        // zero/empty state here would wipe out whatever this row was already correctly showing
        // (the last persisted record, or a previous handle's progress) for the brief window until
        // the handle's own first ProgressChanged/ChunksChanged arrives with the real values - the
        // resume-time "jump to empty and back" from #10. Leaving the row's current display alone
        // until there's real data to show avoids that; a genuinely fresh download has nothing
        // worth preserving here anyway; since the row was just created at 0, this is a no-op.
        // Gated on Chunks (populated by InitializeChunks right after HEAD, regardless of whether
        // the size turned out to be known) rather than TotalBytes > 0 - a download whose server
        // never reports Content-Length legitimately keeps TotalBytes at 0 while it runs, and that
        // must not be mistaken for "HEAD hasn't come back yet".
        if (handle.Chunks.Count > 0)
        {
            BytesDownloaded = handle.BytesDownloaded;
            TotalBytes = handle.TotalBytes;
            SyncChunksFrom(handle.Chunks);
        }

        handle.ProgressChanged += OnProgressChanged;
        handle.ChunksChanged += OnChunksChanged;
        handle.LogMessage += OnLogMessage;
    }

    /// <summary>Unhooks any live handle's events. Called by <see cref="DownloadListViewModel"/>
    /// when this row is dropped from the list (removed downstream, or no longer present in a
    /// reconciliation snapshot) so the row doesn't keep receiving events after it's discarded.</summary>
    public void Detach()
    {
        if (_handle is null)
            return;

        _handle.ProgressChanged -= OnProgressChanged;
        _handle.ChunksChanged -= OnChunksChanged;
        _handle.LogMessage -= OnLogMessage;
        _handle = null;
        HasActiveHandle = false;
    }

    /// <summary>Refreshes the persisted-only fields from a fresh repository snapshot. Skipped
    /// for rows with an active handle - the handle's own events are the authoritative, more
    /// frequently-updated source for those while it's live.</summary>
    public void UpdateFromRecord(DownloadRecord record)
    {
        if (HasActiveHandle)
            return;

        State = record.State;
        BytesDownloaded = record.BytesDownloaded;
        TotalBytes = record.TotalBytes;
    }

    private void OnProgressChanged(object? sender, DownloadProgress progress)
    {
        Dispatcher.UIThread.Post(() =>
        {
            State = progress.State;
            BytesDownloaded = progress.BytesDownloaded;
            TotalBytes = progress.TotalBytes;
            SpeedBytesPerSecond = progress.SpeedBytesPerSecond;

            if (progress.State == DownloadState.Failed)
                LastError ??= "Download failed - see log for details.";

            // This handle is done - most importantly on Failed, where DownloadManager's
            // auto-retry may already be starting a *replacement* handle for this same download
            // internally, with no direct call back into the UI. Detaching here (rather than only
            // on an explicit user action) clears HasActiveHandle so the next reconciliation tick
            // notices this row has no live handle and either picks up that replacement via
            // GetActiveHandle, or - if there isn't one - falls back to the persisted record, so
            // the row can't stay frozen on this now-dead handle's last state indefinitely.
            if (progress.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
                Detach();
        });
    }

    private void OnChunksChanged(object? sender, IReadOnlyList<ChunkProgress> chunks) =>
        Dispatcher.UIThread.Post(() => SyncChunksFrom(chunks));

    private void OnLogMessage(object? sender, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (State == DownloadState.Failed)
                LastError = message;
            else
                _onLogMessage(message);
        });

    /// <summary>Updates <see cref="Chunks"/> in place (matched by index) rather than replacing
    /// the collection, so the name cell's segmented progress bar animates each connection's fill
    /// instead of rebuilding its segments on every tick.</summary>
    private void SyncChunksFrom(IReadOnlyList<ChunkProgress> snapshot)
    {
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (i < Chunks.Count)
                Chunks[i].UpdateFrom(snapshot[i]);
            else
                Chunks.Add(new ChunkRowViewModel(snapshot[i]));
        }

        while (Chunks.Count > snapshot.Count)
            Chunks.RemoveAt(Chunks.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => _handle?.Pause();

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task Resume()
    {
        if (_handle is not null && State == DownloadState.Paused)
        {
            _handle.Resume();
            return;
        }

        // Interrupted (no live handle yet) or Failed (a stale handle from the failed attempt,
        // which Resume() on the handle itself can't restart - only a Paused handle can). Either
        // way, re-add it, which falls through to Downloader's .avadm-footer resume logic and
        // picks up from whatever was already written to disk instead of starting over.
        if (_handle is not null)
            Detach();
        LastError = null;

        var result = await _downloadManager.ResumeDownloadAsync(Id);
        if (result is { Success: true, Handle: not null })
            AttachHandle(result.Handle);
        else
            LastError = result.Error ?? "Resume failed.";
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _onCancelRequested(this);

    partial void OnSpeedLimitBytesPerSecondChanged(long? value) => _handle?.SetSpeedLimit(value);

    [RelayCommand]
    private void Remove() => _onRemoveRequested(this);

    /// <summary>Context-menu "Remove": acts on the whole current selection (the view has already
    /// made sure this row is part of it) - the list view model picks the single- or bulk-remove
    /// dialog by how many rows are selected.</summary>
    [RelayCommand]
    private void ContextRemove() => _onContextRemoveRequested(this);

    /// <summary>Context-menu "Open file" - always opens the file itself (unlike the double-click
    /// gesture, which follows the Settings "double-click action" choice).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDownload))]
    private void OpenFile()
    {
        if (!FileLauncher.OpenFile(DestinationPath))
            _onLogMessage($"Couldn't open \"{FileName}\" - it may have been moved or deleted.");
    }

    /// <summary>Double-click behavior for a completed row, per the Settings page's
    /// "Downloaded item double-click" choice - either open the file itself or reveal its
    /// containing folder. Wired to the header's DoubleTapped gesture in
    /// <c>DownloadRowView.axaml.cs</c>, not to a visible button.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenDownload))]
    private void OpenDownload()
    {
        var opened = _getDoubleClickAction() == DownloadDoubleClickAction.OpenContainingFolder
            ? FileLauncher.OpenContainingFolder(DestinationPath)
            : FileLauncher.OpenFile(DestinationPath);

        if (!opened)
            _onLogMessage($"Couldn't open \"{FileName}\" - it may have been moved or deleted.");
    }

    /// <summary>The row's folder-icon button: always tries to reveal the containing folder,
    /// regardless of download status, unlike <see cref="OpenDownload"/> above.</summary>
    [RelayCommand]
    private void OpenContainingFolder()
    {
        if (!FileLauncher.OpenContainingFolder(DestinationPath))
            _onLogMessage($"Couldn't open the folder for \"{FileName}\" - it may have been moved or deleted.");
    }
}
