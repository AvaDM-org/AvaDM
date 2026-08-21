using System.Collections.ObjectModel;
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
    private readonly DownloadManager _downloadManager;
    private readonly Action<DownloadRowViewModel> _onRemoveRequested;
    private readonly Action<DownloadRowViewModel> _onCancelRequested;
    private readonly Action<string> _onLogMessage;
    private readonly Func<DownloadDoubleClickAction> _getDoubleClickAction;
    private DownloadHandle? _handle;

    public Guid Id { get; }

    [ObservableProperty]
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
    [NotifyPropertyChangedFor(nameof(CanOpenDownload))]
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
    [NotifyPropertyChangedFor(nameof(CanOpenDownload))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDownloadCommand))]
    private bool _hasActiveHandle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private long _bytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(EtaText))]
    private double? _speedBytesPerSecond;

    /// <summary>Last error text, kept even after the row stops being expanded so a Failed row's
    /// reason is visible without expanding it - design.md has no log panel, so this is the only
    /// place the failure reason surfaces.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastError))]
    private string? _lastError;

    /// <summary>Whether <see cref="LastError"/> has text to show. A plain computed bool rather
    /// than an AXAML string-to-visibility converter, matching this codebase's
    /// converter-free-view-model convention (see <c>Converters/FormatHelpers.cs</c>).</summary>
    public bool HasLastError => !string.IsNullOrEmpty(LastError);

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Bound to the inline <see cref="Controls.SpeedLimitEditor"/> shown only while
    /// expanded. Applied to the live handle immediately on change (see
    /// <see cref="OnSpeedLimitBytesPerSecondChanged"/>) rather than needing an explicit Apply
    /// step, since <see cref="DownloadHandle.SetSpeedLimit"/> is cheap to call repeatedly.</summary>
    [ObservableProperty]
    private long? _speedLimitBytesPerSecond;

    public ObservableCollection<ChunkRowViewModel> Chunks { get; } = new();

    public DownloadRowViewModel(
        DownloadManager downloadManager,
        DownloadRecord record,
        DownloadHandle? handle,
        Action<DownloadRowViewModel> onRemoveRequested,
        Action<DownloadRowViewModel> onCancelRequested,
        Action<string> onLogMessage,
        Func<DownloadDoubleClickAction> getDoubleClickAction)
    {
        _downloadManager = downloadManager;
        _onRemoveRequested = onRemoveRequested;
        _onCancelRequested = onCancelRequested;
        _onLogMessage = onLogMessage;
        _getDoubleClickAction = getDoubleClickAction;
        Id = record.Id;
        _fileName = Path.GetFileName(record.DestinationPath);
        _destinationPath = record.DestinationPath;
        _sourceUrl = record.Uri;
        _state = record.State;
        _bytesDownloaded = record.BytesDownloaded;
        _totalBytes = record.TotalBytes;

        if (handle is not null)
            AttachHandle(handle);
    }

    public double ProgressPercent => TotalBytes > 0 ? BytesDownloaded * 100.0 / TotalBytes : 0.0;

    public string BytesText => $"{FormatHelpers.FormatBytes(BytesDownloaded)} / {FormatHelpers.FormatBytes(TotalBytes)}";

    public string SpeedText => HasActiveHandle && State == DownloadState.Running
        ? FormatHelpers.FormatSpeed(SpeedBytesPerSecond)
        : "-";

    public string EtaText => HasActiveHandle && State == DownloadState.Running
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
        BytesDownloaded = handle.BytesDownloaded;
        TotalBytes = handle.TotalBytes;
        SpeedLimitBytesPerSecond = handle.SpeedLimitBytesPerSecond;
        SyncChunksFrom(handle.Chunks);

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
    /// the collection, so an expanded chunk panel doesn't flicker/rebuild on every tick.</summary>
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

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnSpeedLimitBytesPerSecondChanged(long? value) => _handle?.SetSpeedLimit(value);

    [RelayCommand]
    private void Remove() => _onRemoveRequested(this);

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
