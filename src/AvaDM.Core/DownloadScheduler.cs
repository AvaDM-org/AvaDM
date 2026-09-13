using System.Collections.Concurrent;

namespace AvaDM.Core;

/// <summary>
/// Owns the in-memory timers for every <see cref="DownloadState.Scheduled"/> download: one
/// capped-delay wait loop per scheduled item, keyed by download id. Never starts a transfer
/// itself - the only thing it ever does, once a schedule comes due, is invoke the
/// <c>onDueAsync</c> callback it was constructed with (wired by <see cref="DownloadManager"/> to
/// flip the row to <see cref="DownloadState.Queued"/> and run a normal admission pass), so the
/// concurrency limit from the download queue is always honored even for a scheduled item that
/// comes due while the queue is already full.
/// </summary>
public sealed class DownloadScheduler
{
    /// <summary><see cref="Task.Delay(TimeSpan)"/>'s cap is ~24.8 days (<c>int.MaxValue</c> ms) -
    /// too short for a far-future schedule. <see cref="Arm"/> loops in chunks of at most this
    /// long, rechecking the actual remaining time on every wake, rather than assuming a single
    /// delay this long means the schedule is due.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly Func<Guid, Task> _onDueAsync;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _registry = new();

    public DownloadScheduler(Func<Guid, Task> onDueAsync, TimeProvider? timeProvider = null)
    {
        _onDueAsync = onDueAsync;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Arms (or re-arms) a wait loop for <paramref name="id"/> that resolves at
    /// <paramref name="dueUtc"/>, then invokes the due callback. A <paramref name="dueUtc"/>
    /// already in the past resolves immediately on the first check - no special-casing needed,
    /// which is what makes this safe to call unconditionally for every persisted
    /// <see cref="DownloadState.Scheduled"/> row at startup, including one whose time elapsed
    /// while the app was closed. Fire-and-forget: the caller doesn't await the wait itself, only
    /// (indirectly, via the due callback) its eventual firing.</summary>
    public void Arm(Guid id, DateTime dueUtc)
    {
        var cts = new CancellationTokenSource();
        _registry[id] = cts;
        _ = RunAsync(id, dueUtc, cts);
    }

    /// <summary>Cancels and removes <paramref name="id"/>'s outstanding wait, if any. That same
    /// token is passed to every <see cref="Task.Delay(TimeSpan,TimeProvider,CancellationToken)"/>
    /// call in <see cref="RunAsync"/>'s loop, so this makes the wait throw
    /// <see cref="OperationCanceledException"/> immediately - wherever in the capped-wait loop it
    /// currently is - rather than letting it wake up, recheck, and potentially fire anyway. A
    /// no-op if nothing is armed for this id (already fired, already cancelled, or never armed).</summary>
    public void Cancel(Guid id)
    {
        if (_registry.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task RunAsync(Guid id, DateTime dueUtc, CancellationTokenSource cts)
    {
        var due = new DateTimeOffset(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc));
        try
        {
            while (true)
            {
                var remaining = due - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    break;

                var delay = remaining > MaxDelay ? MaxDelay : remaining;
                await Task.Delay(delay, _timeProvider, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Conditional remove, by reference to this exact CancellationTokenSource: a concurrent
        // Cancel(id) racing the loop's normal exit (or - rarer still - a newer Arm(id, ...) call
        // for the same id, e.g. a fresh schedule replacing this one) must not have its own entry
        // deleted out from under it by this now-stale instance.
        if (((ICollection<KeyValuePair<Guid, CancellationTokenSource>>)_registry).Remove(new(id, cts)))
            await _onDueAsync(id);
    }
}
