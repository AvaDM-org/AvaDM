using AvaDM.Core;
using AvaDM.UI.Converters;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// One chunk of a download, shown in a <see cref="DownloadRowViewModel"/>'s expandable panel
/// per design.md's <c>chunk-row</c> component. Kept as its own row-scoped instance list
/// (created/updated in place from <see cref="DownloadHandle.Chunks"/> snapshots) rather than
/// rebuilt from scratch on every tick, so bound UI elements (e.g. an expanded row) don't churn.
/// </summary>
public sealed partial class ChunkRowViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChunkNumberText))]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBytes))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ByteRangeText))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    private long _startByte;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBytes))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ByteRangeText))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    private long _endByte;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private long _bytesDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusChipClass))]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    [NotifyPropertyChangedFor(nameof(IsSizeUnknown))]
    private ChunkStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double? _speedBytesPerSecond;

    public ChunkRowViewModel(ChunkProgress progress) => UpdateFrom(progress);

    public long TotalBytes => EndByte - StartByte + 1;

    public double ProgressPercent => TotalBytes > 0 ? BytesDownloaded * 100.0 / TotalBytes : 0.0;

    /// <summary>True while this chunk is downloading but its end byte is still the unknown-size
    /// sentinel (see <c>Downloader</c>'s unknown-size fallback and <c>DownloadHandle.ChunkTracker</c>) -
    /// drives this chunk's progress bar into indeterminate mode instead of sitting at 0%.</summary>
    public bool IsSizeUnknown => Status == ChunkStatus.Downloading && TotalBytes <= 0;

    public string ByteRangeText => FormatHelpers.FormatByteRange(StartByte, EndByte);

    public string ChunkNumberText => $"Connection {Index + 1}";

    public string StatusText => Status.ToString();

    /// <summary>"-" once the chunk isn't actively downloading (pending/completed/failed),
    /// matching <see cref="DownloadRowViewModel.SpeedText"/>'s same gating for the aggregate row.</summary>
    public string SpeedText => Status == ChunkStatus.Downloading
        ? FormatHelpers.FormatSpeed(SpeedBytesPerSecond)
        : "-";

    /// <summary>Style-class name for the reusable status chip control, matching the semantic
    /// mapping documented in <c>Styles/StatusChip.axaml</c>.</summary>
    public string StatusChipClass => Status switch
    {
        ChunkStatus.Completed => "success",
        ChunkStatus.Downloading => "info",
        ChunkStatus.Failed => "danger",
        ChunkStatus.Pending => "neutral",
        _ => "neutral",
    };

    /// <summary>Refreshes this row in place from a fresh snapshot - called from
    /// <see cref="DownloadRowViewModel"/>'s <c>ChunksChanged</c> handler instead of replacing the
    /// instance, so the expanded panel's bound elements don't flicker.</summary>
    public void UpdateFrom(ChunkProgress progress)
    {
        Index = progress.Index;
        StartByte = progress.StartByte;
        EndByte = progress.EndByte;
        BytesDownloaded = progress.BytesDownloaded;
        Status = progress.Status;
        SpeedBytesPerSecond = progress.SpeedBytesPerSecond;
    }
}
