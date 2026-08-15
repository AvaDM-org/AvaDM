using System.Diagnostics;

namespace AvaDM.Core;

public enum DownloadState
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public record DownloadOptions
{
    /// <summary>Number of concurrent Range-request chunks. <c>null</c> means use
    /// <see cref="DownloadSettings.DefaultChunkCount"/>.</summary>
    public int? ChunkCount { get; init; }

    /// <summary>Speed limit in bytes per second. <c>null</c> means use
    /// <see cref="DownloadSettings.DefaultSpeedLimitBytesPerSecond"/>.</summary>
    public long? InitialSpeedLimitBytesPerSecond { get; init; }
}

public record DownloadProgress(DownloadState State, long BytesDownloaded, long TotalBytes, double? SpeedBytesPerSecond);

public enum ChunkStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

/// <summary>
/// Point-in-time snapshot of one chunk's byte range and progress within a download. The
/// whole-file fallback path (no Range support) is represented as a single chunk spanning the
/// entire file, so a UI can render chunk progress uniformly regardless of which path ran.
/// </summary>
public sealed record ChunkProgress(int Index, long StartByte, long EndByte, long BytesDownloaded, ChunkStatus Status)
{
    public long TotalBytes => EndByte - StartByte + 1;
}

/// <summary>
/// A live, controllable handle to one in-progress (or finished) download, returned by
/// <see cref="Downloader.StartDownload"/>. This is the surface a UI (or the console REPL)
/// is meant to hold onto: it carries no shared mutable state with any other download, so
/// any number of handles from the same <see cref="Downloader"/> run fully independently.
/// </summary>
public sealed class DownloadHandle
{
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(100);

    private long _bytesDownloaded;
    private long _lastProgressReportTimestamp;
    private long _startTimestamp;
    private volatile DownloadState _state = DownloadState.Pending;
    private volatile ChunkTracker[] _chunkTrackers = [];

    internal DownloadHandle(Uri uri, string destinationPath, DownloadOptions options)
    {
        Uri = uri;
        DestinationPath = destinationPath;
        PauseTokenSource = new PauseTokenSource();
        SpeedLimiter = new SpeedLimiter(options.InitialSpeedLimitBytesPerSecond);
        CancellationTokenSource = new CancellationTokenSource();
    }

    public Uri Uri { get; }
    public string DestinationPath { get; }
    public long TotalBytes { get; internal set; }
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public DownloadState State => _state;

    /// <summary>Snapshot of every chunk's byte range and progress, in chunk order. Empty until
    /// the engine has determined how the file will be split (right after the HEAD request).</summary>
    public IReadOnlyList<ChunkProgress> Chunks
    {
        get
        {
            var trackers = _chunkTrackers;
            var snapshot = new ChunkProgress[trackers.Length];
            for (var i = 0; i < trackers.Length; i++)
                snapshot[i] = trackers[i].ToSnapshot(i);
            return snapshot;
        }
    }

    /// <summary>Completes when the download finishes; faults with the original exception on
    /// failure (including <see cref="OperationCanceledException"/> on cancellation).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public event EventHandler<DownloadProgress>? ProgressChanged;

    /// <summary>Fired alongside <see cref="ProgressChanged"/> (same throttling) whenever chunk
    /// layout or per-chunk progress changes, so a UI can render each chunk's own progress bar
    /// in addition to the aggregate total.</summary>
    public event EventHandler<IReadOnlyList<ChunkProgress>>? ChunksChanged;

    /// <summary>Fired for human-readable, non-progress notices (chunk retries, range-support
    /// fallback, etc.) so a UI/console can surface them without the engine writing directly
    /// to any output stream. Purely informational - never required for correctness.</summary>
    public event EventHandler<string>? LogMessage;

    internal PauseTokenSource PauseTokenSource { get; }
    internal SpeedLimiter SpeedLimiter { get; }
    internal CancellationTokenSource CancellationTokenSource { get; }

    public void Pause()
    {
        if (_state != DownloadState.Running)
            return;

        PauseTokenSource.Pause();
        _state = DownloadState.Paused;
        ReportProgress(force: true);
    }

    public void Resume()
    {
        if (_state != DownloadState.Paused)
            return;

        PauseTokenSource.Resume();
        _state = DownloadState.Running;
        ReportProgress(force: true);
    }

    public void SetSpeedLimit(long? bytesPerSecond) => SpeedLimiter.SetLimit(bytesPerSecond);

    public void Cancel() => CancellationTokenSource.Cancel();

    /// <summary>Kicks off <paramref name="run"/> against this handle's cancellation token and
    /// wires up <see cref="Completion"/>/<see cref="State"/>. The returned task is "hot" - it
    /// starts executing up to its first await before this method returns, so the caller can
    /// return the handle immediately without waiting on the download.</summary>
    internal void Start(Func<CancellationToken, Task> run)
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        _state = DownloadState.Running;
        Completion = RunAndTrackAsync(run);
    }

    private async Task RunAndTrackAsync(Func<CancellationToken, Task> run)
    {
        try
        {
            await run(CancellationTokenSource.Token);
            _state = DownloadState.Completed;
        }
        catch (OperationCanceledException)
        {
            _state = DownloadState.Cancelled;
            throw;
        }
        catch
        {
            _state = DownloadState.Failed;
            throw;
        }
        finally
        {
            ReportProgress(force: true);
        }
    }

    internal void Log(string message) => LogMessage?.Invoke(this, message);

    /// <summary>Establishes the chunk layout (index, byte range, initial Pending status) before
    /// any chunk task starts writing. Called once per download - by <see cref="Downloader"/>
    /// with one entry per concurrent Range chunk, or with a single whole-file entry when the
    /// server doesn't support Range requests.</summary>
    internal void InitializeChunks(IReadOnlyList<(long Start, long End)> ranges)
    {
        var trackers = new ChunkTracker[ranges.Count];
        for (var i = 0; i < ranges.Count; i++)
            trackers[i] = new ChunkTracker(ranges[i].Start, ranges[i].End);
        _chunkTrackers = trackers;
        ReportProgress(force: true);
    }

    internal void SetChunkStatus(int chunkIndex, ChunkStatus status)
    {
        _chunkTrackers[chunkIndex].SetStatus(status);
        ReportProgress(force: true);
    }

    /// <summary>Records bytes written for one chunk and folds them into the download's overall
    /// total in a single call, so per-chunk and aggregate progress never drift apart.</summary>
    internal void AddChunkBytesDownloaded(int chunkIndex, int byteCount)
    {
        _chunkTrackers[chunkIndex].AddBytes(byteCount);
        Interlocked.Add(ref _bytesDownloaded, byteCount);
        ReportProgress();
    }

    internal void ReportProgress(bool force = false)
    {
        if (ProgressChanged is null && ChunksChanged is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force)
        {
            var last = Interlocked.Read(ref _lastProgressReportTimestamp);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ProgressReportInterval)
                return;
        }
        Interlocked.Exchange(ref _lastProgressReportTimestamp, now);

        ProgressChanged?.Invoke(this, new DownloadProgress(State, BytesDownloaded, TotalBytes, ComputeAverageSpeed(now)));
        ChunksChanged?.Invoke(this, Chunks);
    }

    // Average throughput since the download started, not an instantaneous rate - simple and
    // good enough for a status line; revisit with a sliding window if that's ever not enough.
    private double? ComputeAverageSpeed(long now)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp, now);
        return elapsed.TotalSeconds > 0 ? BytesDownloaded / elapsed.TotalSeconds : null;
    }

    /// <summary>Mutable per-chunk state, updated concurrently by that chunk's own download task
    /// (never by any other chunk's task). The byte range is fixed at construction; only bytes
    /// downloaded and status change afterward, both via lock-free atomics so reading a snapshot
    /// (<see cref="ToSnapshot"/>) never blocks the writer.</summary>
    private sealed class ChunkTracker(long start, long end)
    {
        private long _bytesDownloaded;
        private volatile int _status = (int)ChunkStatus.Pending;

        public void AddBytes(int count) => Interlocked.Add(ref _bytesDownloaded, count);
        public void SetStatus(ChunkStatus status) => _status = (int)status;

        public ChunkProgress ToSnapshot(int index) =>
            new(index, start, end, Interlocked.Read(ref _bytesDownloaded), (ChunkStatus)_status);
    }
}
