using System.Diagnostics;

namespace AvaDM.Core;

/// <summary>
/// Sliding-window throughput estimator: reports bytes/sec averaged over a short recent window
/// instead of over the whole lifetime of what it's measuring, so the reported speed tracks what
/// is actually happening right now rather than slowly drifting toward a lifetime average that
/// lags every real change in rate. One instance measures one growing byte counter - a download's
/// aggregate total, or a single chunk's - and is fed via <see cref="AddSample"/> each time that
/// counter is reported.
///
/// Thread-safe: <see cref="AddSample"/> is guarded by an internal lock, since a download's
/// aggregate tracker (and, while multiple chunks share a snapshot pass, an individual chunk's
/// tracker) can be sampled from whichever chunk task most recently reported progress while
/// another chunk's task is concurrently doing the same for its own tracker.
/// </summary>
internal sealed class SpeedTracker
{
    private static readonly TimeSpan DefaultWindowSpan = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _windowSpan;
    private readonly Lock _lock = new();
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();

    /// <param name="windowSpan">How far back the rate is averaged. Defaults to 3 seconds -
    /// short enough to react quickly to a real change in throughput, long enough to smooth out
    /// per-read jitter. Overridable so tests can use a short window instead of sleeping for
    /// several real seconds.</param>
    public SpeedTracker(TimeSpan? windowSpan = null) => _windowSpan = windowSpan ?? DefaultWindowSpan;

    /// <summary>Records a new (now, cumulative-bytes) sample and returns the throughput averaged
    /// between the oldest sample still inside the window and this one. <c>null</c> until a
    /// second sample lands - one point alone has no elapsed time to divide by.</summary>
    public double? AddSample(long cumulativeBytes)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _samples.Enqueue((now, cumulativeBytes));

            while (_samples.Count > 1 && Stopwatch.GetElapsedTime(_samples.Peek().Timestamp, now) > _windowSpan)
                _samples.Dequeue();

            var (oldestTimestamp, oldestBytes) = _samples.Peek();
            var elapsedSeconds = Stopwatch.GetElapsedTime(oldestTimestamp, now).TotalSeconds;
            return elapsedSeconds > 0 ? (cumulativeBytes - oldestBytes) / elapsedSeconds : null;
        }
    }
}
