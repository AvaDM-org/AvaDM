using System.Diagnostics;

namespace AvaDM.Core;

/// <summary>
/// Token-bucket throttle shared across every chunk task of a single download, so a speed
/// limit applies to the download's total throughput rather than being multiplied by the
/// chunk count. One instance per <see cref="DownloadHandle"/>; safe to call concurrently
/// from multiple chunk tasks, and <see cref="SetLimit"/> can be changed mid-download.
///
/// The lock only guards accounting (available tokens, last-refill timestamp) - the actual
/// wait is a <see cref="Task.Delay(TimeSpan, CancellationToken)"/> taken outside the lock,
/// so concurrent callers never serialize on the delay itself, only on the brief bookkeeping.
/// </summary>
internal sealed class SpeedLimiter
{
    private readonly Lock _lock = new();
    private long? _bytesPerSecond;
    private double _availableTokens;
    private long _lastRefillTimestamp;

    public SpeedLimiter(long? initialBytesPerSecond = null)
    {
        _bytesPerSecond = initialBytesPerSecond;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Current limit, or <c>null</c> if unlimited - lets a UI reflect the live value
    /// (e.g. after a resumed download seeds it from <see cref="DownloadOptions"/>) rather than
    /// only ever being able to set it.</summary>
    public long? CurrentLimitBytesPerSecond
    {
        get
        {
            lock (_lock)
                return _bytesPerSecond;
        }
    }

    public void SetLimit(long? bytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = bytesPerSecond;
            // Reset the bucket on every limit change: an old accumulated/deficit token count
            // computed under a different rate (or no rate) isn't meaningful under the new one.
            _availableTokens = 0;
            _lastRefillTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public async Task WaitForTokensAsync(int byteCount, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan? delay = TryConsumeOrGetDelay(byteCount);
            if (delay is null)
                return;

            await Task.Delay(delay.Value, cancellationToken);
            // Loop back and re-check: the limit may have changed (or been lifted) during the delay.
        }
    }

    private TimeSpan? TryConsumeOrGetDelay(int byteCount)
    {
        lock (_lock)
        {
            if (_bytesPerSecond is not { } limit)
                return null; // Unlimited - proceed immediately, no wait.

            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastRefillTimestamp, now);
            _lastRefillTimestamp = now;
            // Cap the bucket at one second's worth of allowance so a long idle/unlimited
            // stretch can't accumulate an unbounded burst once a limit is (re)applied.
            _availableTokens = Math.Min(limit, _availableTokens + elapsed.TotalSeconds * limit);

            if (_availableTokens >= byteCount)
            {
                _availableTokens -= byteCount;
                return null;
            }

            var deficit = byteCount - _availableTokens;
            return TimeSpan.FromSeconds(deficit / limit);
        }
    }
}
