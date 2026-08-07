namespace AvaDM.Core;

/// <summary>
/// Cooperative async pause gate (Stephen Toub's PauseTokenSource pattern). While paused,
/// <see cref="WaitWhilePausedAsync"/> awaits a shared <see cref="TaskCompletionSource"/>
/// instead of polling, so any number of concurrent callers (e.g. one per download chunk)
/// can wait on the same pause/resume signal for free - no allocation on the not-paused path.
/// </summary>
internal sealed class PauseTokenSource
{
    private volatile TaskCompletionSource? _pauseCompletionSource;

    public bool IsPaused => _pauseCompletionSource is not null;

    public void Pause()
    {
        // CAS so a second Pause() call while already paused is a harmless no-op rather than
        // replacing (and thereby leaking/never-completing) the in-flight TaskCompletionSource.
        Interlocked.CompareExchange(
            ref _pauseCompletionSource,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public void Resume()
    {
        var tcs = Interlocked.Exchange(ref _pauseCompletionSource, null);
        tcs?.TrySetResult();
    }

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _pauseCompletionSource;
        return tcs is null ? Task.CompletedTask : tcs.Task.WaitAsync(cancellationToken);
    }
}
