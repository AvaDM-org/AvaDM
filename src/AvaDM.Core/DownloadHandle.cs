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
    public int ChunkCount { get; init; } = 5;
    public long? InitialSpeedLimitBytesPerSecond { get; init; }
}

public record DownloadProgress(DownloadState State, long BytesDownloaded, long TotalBytes, double? SpeedBytesPerSecond);

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

    /// <summary>Completes when the download finishes; faults with the original exception on
    /// failure (including <see cref="OperationCanceledException"/> on cancellation).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public event EventHandler<DownloadProgress>? ProgressChanged;

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

    internal void AddBytesDownloaded(int byteCount)
    {
        Interlocked.Add(ref _bytesDownloaded, byteCount);
        ReportProgress();
    }

    internal void ReportProgress(bool force = false)
    {
        if (ProgressChanged is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force)
        {
            var last = Interlocked.Read(ref _lastProgressReportTimestamp);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < ProgressReportInterval)
                return;
        }
        Interlocked.Exchange(ref _lastProgressReportTimestamp, now);

        ProgressChanged.Invoke(this, new DownloadProgress(State, BytesDownloaded, TotalBytes, ComputeAverageSpeed(now)));
    }

    // Average throughput since the download started, not an instantaneous rate - simple and
    // good enough for a status line; revisit with a sliding window if that's ever not enough.
    private double? ComputeAverageSpeed(long now)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp, now);
        return elapsed.TotalSeconds > 0 ? BytesDownloaded / elapsed.TotalSeconds : null;
    }
}
