using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;

namespace AvaDM.Core;

/// <summary>How to proceed when <see cref="DownloadManager.AddDownloadAsync"/> finds an existing
/// index row for the same (Uri, DestinationPath) identity. Left to the caller (console/UI) to
/// decide - the manager never guesses.</summary>
public abstract class ConflictResolution
{
    /// <summary>Resume the existing download. <see cref="Downloader"/> itself detects and resumes
    /// from the <c>.avadm</c> sidecar if one is present; if it's missing or corrupt it logs and
    /// starts fresh rather than throwing.</summary>
    public sealed class Resume : ConflictResolution;

    /// <summary>Discard any existing <c>.avadm</c> progress and start over from byte 0.</summary>
    public sealed class Overwrite : ConflictResolution;

    /// <summary>Keep the existing download untouched and start a new, independent one at a
    /// different destination.</summary>
    public sealed class RenameDestination(string newPath) : ConflictResolution
    {
        public string NewPath { get; } = newPath;
    }
}

public sealed record AddDownloadResult(bool Success, Guid? Id, DownloadHandle? Handle, ConflictCheckResult? Conflict, string? Error = null);

/// <summary>
/// UI-agnostic orchestration layer that <see cref="Downloader"/> callers (console, and eventually
/// Avalonia) are meant to use instead of talking to <see cref="Downloader"/> directly. Owns the
/// SQLite download index: dedupe-on-add via a conflict-check/resolve API, and keeping index rows
/// in sync with each <see cref="DownloadHandle"/>'s progress for the lifetime of the process.
/// </summary>
public sealed class DownloadManager
{
    private readonly Downloader _downloader;
    private readonly DownloadRepository _repository;
    private readonly DownloadSettings _settings;
    private readonly ConcurrentDictionary<Guid, DownloadHandle> _activeHandles = new();
    private readonly ConcurrentDictionary<Guid, int> _autoRetryAttempts = new();

    /// <summary>Options a still-<see cref="DownloadState.Queued"/> row was originally added/resumed
    /// with, kept only in memory until <see cref="AdmitQueuedDownloadsAsync"/> starts it - not
    /// persisted, so a row still queued across an app restart falls back to default options when
    /// it finally starts. That already matches how every other resume path in this class behaves:
    /// <see cref="ResumeDownloadCoreAsync"/> has always passed <c>options: null</c>.</summary>
    private readonly ConcurrentDictionary<Guid, DownloadOptions?> _queuedOptions = new();

    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>Serializes every decision that reads-then-acts on "how many downloads are running
    /// right now" - <see cref="AddDownloadAsync"/>'s own start-or-queue check and
    /// <see cref="AdmitQueuedDownloadsAsync"/>'s draining loop - so two callers racing each other
    /// (e.g. several <see cref="AddDownloadAsync"/> calls in quick succession, or an admission pass
    /// running at the same moment as a new add) can never both see a free slot and both take it,
    /// running the process over <see cref="DownloadSettings.DefaultMaxConcurrentDownloads"/>.</summary>
    private readonly SemaphoreSlim _admissionLock = new(1, 1);
    private bool _initialized;

    public DownloadManager(HttpClient client, DownloadSettings settings)
    {
        _settings = settings;
        _downloader = new Downloader(client, settings);
        _repository = new DownloadRepository(settings.GetResolvedRepositoryPath());
    }

    /// <summary>Resolves the destination path the same way <see cref="AddDownloadAsync"/> would,
    /// and reports whether an index row already exists for it - without starting anything. Useful
    /// for a caller that wants to ask the user before committing to a resolution.</summary>
    public async Task<ConflictCheckResult> CheckConflictAsync(Uri uri, string? destinationPath)
    {
        await EnsureInitializedAsync();
        var resolvedPath = ResolvePath(uri, destinationPath);
        return await _repository.CheckConflictAsync(uri.AbsoluteUri, resolvedPath);
    }

    /// <summary>Starts a download, first checking the index for a conflicting (Uri, DestinationPath)
    /// row. With no conflict, or once <paramref name="resolution"/> has been applied, delegates to
    /// <see cref="Downloader.StartDownload"/> and keeps the index in sync with the resulting handle
    /// for the rest of its life.</summary>
    public async Task<AddDownloadResult> AddDownloadAsync(
        Uri uri, string? destinationPath, DownloadOptions? options = null, ConflictResolution? resolution = null)
    {
        await EnsureInitializedAsync();

        var resolvedPath = ResolvePath(uri, destinationPath);
        var conflict = await _repository.CheckConflictAsync(uri.AbsoluteUri, resolvedPath);

        if (conflict.HasConflict)
        {
            if (resolution is null)
                return new AddDownloadResult(false, null, null, conflict);

            if (resolution is ConflictResolution.Resume)
            {
                if (conflict.ExistingRecord!.State == DownloadState.Completed)
                    return new AddDownloadResult(false, null, null, conflict, "Download already completed.");
                // Fall through: Downloader detects and resumes from the .avadm sidecar itself.
            }
            else if (resolution is ConflictResolution.Overwrite)
            {
                var workingPath = resolvedPath + ".avadm";
                if (File.Exists(workingPath))
                    File.Delete(workingPath);
            }
            else if (resolution is ConflictResolution.RenameDestination rename)
            {
                var renamedPath = ResolvePath(uri, rename.NewPath);
                var renameConflict = await _repository.CheckConflictAsync(uri.AbsoluteUri, renamedPath);
                if (renameConflict.HasConflict)
                    return new AddDownloadResult(false, null, null, renameConflict);
                resolvedPath = renamedPath;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown conflict resolution.");
            }
        }

        // Resume/Overwrite restart the *same* (Uri, DestinationPath) row that CheckConflictAsync
        // already found - e.g. paused, app closed, reopened, resumed. If a handle from an earlier
        // run of that same row is still active here (most notably: the UI never learns about the
        // replacement handle TryAutoRetryAsync starts after a failure, so a user who sees a
        // seemingly-stuck row and clicks Resume could otherwise race a second handle against it),
        // it must be fully stopped before a new one opens the same .avadm file - otherwise two
        // handles end up writing/checkpointing/finalizing the same file concurrently.
        if (conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite)
        {
            var staleHandle = GetActiveHandle(conflict.ExistingRecord!.Id);
            if (staleHandle is not null)
            {
                staleHandle.Cancel();
                try
                {
                    await staleHandle.Completion;
                }
                catch (OperationCanceledException)
                {
                    // Expected: Cancel() faults Completion with this.
                }
            }
        }

        // This must update the existing row in place (same Id) rather than INSERT a second row,
        // which would trip the UNIQUE(Uri, DestinationPath) constraint and, even if it didn't,
        // would hand back a new Id that orphans a UI row already keyed on the old one.
        // RenameDestination targets a path already confirmed conflict-free above, so it always
        // gets a fresh row.
        var id = conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite
            ? conflict.ExistingRecord!.Id
            : Guid.NewGuid();

        // Only actually starts the transfer (and only then calls Downloader.StartDownload) if
        // there's a free concurrency slot right now; otherwise the row below is persisted as
        // Queued and AdmitQueuedDownloadsAsync starts it later once one opens up.
        var handle = await TryStartHandleAsync(uri, resolvedPath, options);
        var state = handle is null ? DownloadState.Queued : DownloadState.Running;

        if (conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite)
            await _repository.ResetForRestartAsync(id, state, handle?.TotalBytes ?? 0);
        else
            await _repository.InsertAsync(id, uri.AbsoluteUri, resolvedPath, state, handle?.TotalBytes ?? 0);

        if (handle is null)
        {
            _queuedOptions[id] = options;
        }
        else
        {
            _activeHandles[id] = handle;
            SyncToRepository(id, handle);
        }

        return new AddDownloadResult(true, id, handle, null);
    }

    private int RunningHandleCount() => _activeHandles.Values.Count(h => h.State == DownloadState.Running);

    /// <summary>Starts a transfer only if there's room under
    /// <see cref="DownloadSettings.DefaultMaxConcurrentDownloads"/> right now, returning
    /// <c>null</c> instead of a handle when there isn't. A <see cref="DownloadState.Paused"/>
    /// handle doesn't count against the limit - pausing frees its slot for a queued download to
    /// start, and it only reclaims one for itself the next time it's resumed.</summary>
    private async Task<DownloadHandle?> TryStartHandleAsync(Uri uri, string resolvedPath, DownloadOptions? options)
    {
        await _admissionLock.WaitAsync();
        try
        {
            return RunningHandleCount() >= _settings.DefaultMaxConcurrentDownloads
                ? null
                : _downloader.StartDownload(uri, resolvedPath, options);
        }
        finally
        {
            _admissionLock.Release();
        }
    }

    /// <summary>Starts as many <see cref="DownloadState.Queued"/> rows, in <c>QueueOrder</c>, as
    /// there's room for under <see cref="DownloadSettings.DefaultMaxConcurrentDownloads"/>. This
    /// and <see cref="AddDownloadAsync"/>'s own fast path (via <see cref="TryStartHandleAsync"/>)
    /// are the only places that ever call <see cref="Downloader.StartDownload"/> - so every event
    /// that can free or add a slot funnels through one of the two: a download finishing, failing,
    /// or being cancelled and a running download being paused call this method (see
    /// <see cref="FinalizeDownloadAsync"/> and the pause branch in <see cref="SyncToRepository"/>);
    /// a settings change to the concurrency limit, a queue reorder, or a bulk/manual resume are
    /// expected to call this too. Public because those last three are driven from outside this
    /// class (the Settings page, a queue-reorder action, <see cref="ResumeAllInterruptedAsync"/>).</summary>
    public async Task AdmitQueuedDownloadsAsync()
    {
        await EnsureInitializedAsync();
        await _admissionLock.WaitAsync();
        try
        {
            var limit = _settings.DefaultMaxConcurrentDownloads;
            var running = RunningHandleCount();
            if (running >= limit)
                return;

            var queued = (await _repository.GetAllAsync())
                .Where(r => r.State == DownloadState.Queued)
                .OrderBy(r => r.QueueOrder);

            foreach (var record in queued)
            {
                if (running >= limit)
                    break;

                _queuedOptions.TryRemove(record.Id, out var options);
                var handle = _downloader.StartDownload(new Uri(record.Uri), record.DestinationPath, options);
                await _repository.UpdateStateAsync(record.Id, DownloadState.Running);
                _activeHandles[record.Id] = handle;
                SyncToRepository(record.Id, handle);
                running++;
            }
        }
        finally
        {
            _admissionLock.Release();
        }
    }

    public async Task<IReadOnlyList<DownloadRecord>> GetAllDownloadsAsync()
    {
        await EnsureInitializedAsync();
        return await _repository.GetAllAsync();
    }

    public async Task<DownloadRecord?> GetDownloadAsync(Guid id)
    {
        await EnsureInitializedAsync();
        return await _repository.GetByIdAsync(id);
    }

    /// <summary>The live handle for a download still running in this process, or <c>null</c> if
    /// it isn't (finished, or started in a previous process and never resumed here).</summary>
    public DownloadHandle? GetActiveHandle(Guid id) => _activeHandles.GetValueOrDefault(id);

    /// <summary>Removes a download from the index, cancelling it first if it's still active in
    /// this process. Optionally also deletes the file(s) left on disk - the final destination if
    /// the download completed, and/or the <c>.avadm</c> working file if it didn't (whichever is
    /// present). File-delete failures are reported back rather than swallowed, since silently
    /// leaving a file behind (or failing to remove one the user asked to remove) is something the
    /// caller needs to be able to surface.</summary>
    public async Task<(bool Success, string? Error)> RemoveDownloadAsync(Guid id, bool deleteFile, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var record = await _repository.GetByIdAsync(id);
        if (record is null)
            return (false, "No download found with that id.");

        var handle = GetActiveHandle(id);
        if (handle is not null)
        {
            handle.Cancel();
            try
            {
                await handle.Completion;
            }
            catch (OperationCanceledException)
            {
                // Expected: Cancel() faults Completion with this.
            }
        }

        await _repository.DeleteAsync(id);
        _autoRetryAttempts.TryRemove(id, out _);
        _queuedOptions.TryRemove(id, out _);

        if (deleteFile)
        {
            try
            {
                if (File.Exists(record.DestinationPath))
                    File.Delete(record.DestinationPath);

                var workingPath = record.DestinationPath + ".avadm";
                if (File.Exists(workingPath))
                    File.Delete(workingPath);
            }
            catch (Exception ex)
            {
                return (false, $"Removed from index, but failed to delete file(s): {ex.Message}");
            }
        }

        return (true, null);
    }

    /// <summary>Cancels a download that's still active in this process and deletes its
    /// <c>.avadm</c> working file - i.e. the download's progress is not resumable afterwards.
    /// Unlike <see cref="RemoveDownloadAsync"/>, the index row is left in place (recorded as
    /// Cancelled) rather than deleted, so the row still shows up in the list.</summary>
    public async Task<(bool Success, string? Error)> CancelDownloadAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();

        var record = await _repository.GetByIdAsync(id);
        if (record is null)
            return (false, "No download found with that id.");

        var handle = GetActiveHandle(id);
        if (handle is not null)
        {
            handle.Cancel();
            try
            {
                await handle.Completion;
            }
            catch (OperationCanceledException)
            {
                // Expected: Cancel() faults Completion with this.
            }
        }
        else
        {
            // No live handle - a Queued row (or one left over from a previous process) has no
            // Completion continuation to record the cancellation for it, so this is the only
            // place that ever will.
            await _repository.UpdateStateAsync(id, DownloadState.Cancelled);
        }

        _autoRetryAttempts.TryRemove(id, out _);
        _queuedOptions.TryRemove(id, out _);

        try
        {
            var workingPath = record.DestinationPath + ".avadm";
            if (File.Exists(workingPath))
                File.Delete(workingPath);
        }
        catch (Exception ex)
        {
            return (false, $"Download cancelled, but failed to delete progress file: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>Convenience wrapper for resuming a download that isn't live in this process (e.g.
    /// after an app restart, or after <see cref="DownloadState.Failed"/>): looks up its record and
    /// re-adds it with <see cref="ConflictResolution.Resume"/>, which falls through to
    /// <see cref="Downloader"/>'s existing <c>.avadm</c>-footer resume logic - no separate
    /// "rehydration" code path needed. A manual call always resets the automatic-retry counter
    /// (see <see cref="TryAutoRetryAsync"/>), so a user who resumes by hand never runs out of
    /// retries even if the automatic budget was already exhausted.</summary>
    public async Task<AddDownloadResult> ResumeDownloadAsync(Guid id)
    {
        _autoRetryAttempts.TryRemove(id, out _);
        return await ResumeDownloadCoreAsync(id);
    }

    private async Task<AddDownloadResult> ResumeDownloadCoreAsync(Guid id)
    {
        await EnsureInitializedAsync();

        var record = await _repository.GetByIdAsync(id);
        if (record is null)
            return new AddDownloadResult(false, null, null, null, "No download found with that id.");

        return await AddDownloadAsync(new Uri(record.Uri), record.DestinationPath, null, new ConflictResolution.Resume());
    }

    /// <summary>Moves a queued download one place earlier in the queue, swapping <c>QueueOrder</c>
    /// with whichever queued row is currently ahead of it. Returns <c>false</c> (a no-op) if the
    /// row isn't found, isn't <see cref="DownloadState.Queued"/>, or is already at the front.</summary>
    public Task<bool> MoveQueuedDownloadUpAsync(Guid id) => SwapQueueOrderWithNeighborAsync(id, offset: -1);

    /// <summary>Same as <see cref="MoveQueuedDownloadUpAsync"/>, one place later instead.</summary>
    public Task<bool> MoveQueuedDownloadDownAsync(Guid id) => SwapQueueOrderWithNeighborAsync(id, offset: 1);

    private async Task<bool> SwapQueueOrderWithNeighborAsync(Guid id, int offset)
    {
        await EnsureInitializedAsync();

        var queued = (await _repository.GetAllAsync())
            .Where(r => r.State == DownloadState.Queued)
            .OrderBy(r => r.QueueOrder)
            .ToList();

        var index = queued.FindIndex(r => r.Id == id);
        var neighborIndex = index + offset;
        if (index < 0 || neighborIndex < 0 || neighborIndex >= queued.Count)
            return false;

        var current = queued[index];
        var neighbor = queued[neighborIndex];
        await _repository.UpdateQueueOrderAsync(current.Id, neighbor.QueueOrder);
        await _repository.UpdateQueueOrderAsync(neighbor.Id, current.QueueOrder);
        return true;
    }

    /// <summary>Bulk-resumes every download left <see cref="DownloadState.Pending"/>,
    /// <see cref="DownloadState.Running"/>, <see cref="DownloadState.Paused"/>, or
    /// <see cref="DownloadState.Queued"/> by a previous process - i.e. every row with no live
    /// handle in this one, which is exactly what the UI shows with its derived "Interrupted"
    /// status. Flips each straight to <see cref="DownloadState.Queued"/>, preserving its
    /// <c>QueueOrder</c> so a download's place in line survives the round trip, then runs one
    /// <see cref="AdmitQueuedDownloadsAsync"/> pass. Backs both the manual "Resume downloads" UI
    /// action and <see cref="DownloadSettings.AutoResumeDownloadsOnStartup"/> (see
    /// <see cref="EnsureInitializedAsync"/>), so there's one code path for both instead of two.</summary>
    public async Task ResumeAllInterruptedAsync()
    {
        await EnsureInitializedAsync();

        var interrupted = (await _repository.GetAllAsync())
            .Where(r => r.State is DownloadState.Pending or DownloadState.Running or DownloadState.Paused or DownloadState.Queued)
            .Where(r => GetActiveHandle(r.Id) is null);

        foreach (var record in interrupted)
            await _repository.UpdateStateAsync(record.Id, DownloadState.Queued);

        await AdmitQueuedDownloadsAsync();
    }

    private string ResolvePath(Uri uri, string? destinationPath) =>
        Path.GetFullPath(_downloader.ResolveDestinationPath(uri, destinationPath));

    /// <summary>Wires a freshly-started handle's events to the repository, throttled independently
    /// of the handle's own ~100ms UI progress cadence - SQLite is single-writer, so this must not
    /// fire on every progress tick.</summary>
    private void SyncToRepository(Guid id, DownloadHandle handle)
    {
        long lastDbWriteTimestamp = 0;
        handle.ProgressChanged += (_, progress) =>
        {
            // Unthrottled, unlike the DB write below: pausing frees this handle's concurrency
            // slot, and a queued download waiting on that slot shouldn't have to wait out the
            // same 3-second window the DB write throttles on. ProgressChanged only fires once per
            // Pause() call (no further progress ticks arrive while paused), so this can't fire
            // repeatedly for one pause.
            if (progress.State == DownloadState.Paused)
                _ = AdmitQueuedDownloadsAsync();

            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref lastDbWriteTimestamp);
            if (last != 0 && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(3))
                return;
            Interlocked.Exchange(ref lastDbWriteTimestamp, now);

            _ = _repository.UpdateProgressAsync(id, progress.State, progress.BytesDownloaded, progress.TotalBytes)
                .ContinueWith(
                    t => handle.Log($"DB update failed: {t.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
        };

        // Runs regardless of throttling and regardless of success/failure/cancellation, so the
        // terminal state is always recorded even if the last throttled write is stale.
        handle.Completion.ContinueWith(t =>
        {
            // Reading Exception is what marks a faulted antecedent "observed" - without it, the
            // fault sits unobserved until the GC finalizes the Task, and the runtime rethrows it
            // on the finalizer thread, which the global TaskScheduler.UnobservedTaskException
            // handler then logs as an alarming "Unobserved task exception" even though the
            // failure is already handled cleanly below via handle.State.
            if (t.Exception is not null)
                handle.Log($"Download failed: {t.Exception.GetBaseException().Message}");

            // FinalizeDownloadAsync catches and logs its own exceptions, so discarding the task
            // here (rather than awaiting it) is intentional, not an oversight.
            _ = FinalizeDownloadAsync(id, handle);
        });
    }

    private async Task FinalizeDownloadAsync(Guid id, DownloadHandle handle)
    {
        try
        {
            await _repository.UpdateProgressAsync(id, handle.State, handle.BytesDownloaded, handle.TotalBytes);
        }
        catch (Exception ex)
        {
            handle.Log($"DB update failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            // Conditional remove: if AddDownloadAsync has already raced in a replacement handle
            // for this id (see its stale-handle guard) by the time this finally runs, a plain
            // TryRemove(id) would delete that newer entry out from under it rather than this
            // (now-stale) one.
            ((ICollection<KeyValuePair<Guid, DownloadHandle>>)_activeHandles).Remove(new(id, handle));
        }

        // A terminal handle (Completed/Failed/Cancelled) always frees its slot - let the next
        // queued download in line take it before deciding whether to auto-retry this one, so a
        // failing download's auto-retry queues behind everything already waiting rather than
        // cutting in front of it (it goes through AddDownloadAsync just like a manual resume,
        // which is itself concurrency-aware).
        _ = AdmitQueuedDownloadsAsync();

        if (handle.State == DownloadState.Failed)
            await TryAutoRetryAsync(id, handle);
    }

    /// <summary>Automatically resumes a download that ended in <see cref="DownloadState.Failed"/>,
    /// up to <see cref="DownloadSettings.DefaultAutoRetryAttempts"/> consecutive automatic attempts
    /// for this download id - each one continues from the <c>.avadm</c> footer, so no progress is
    /// lost between attempts. Never counts against, or is reset by, a manual
    /// <see cref="ResumeDownloadAsync"/> call except to clear the counter (see there); an explicit
    /// user cancel or removal also clears it, so a re-added download always starts with a full
    /// budget.</summary>
    private async Task TryAutoRetryAsync(Guid id, DownloadHandle handle)
    {
        var limit = _settings.DefaultAutoRetryAttempts;
        if (limit <= 0)
            return;

        var attempt = _autoRetryAttempts.AddOrUpdate(id, 1, (_, count) => count + 1);
        if (attempt > limit)
            return;

        handle.Log($"Retrying automatically (attempt {attempt} of {limit})...");

        var result = await ResumeDownloadCoreAsync(id);
        if (!result.Success)
            handle.Log($"Automatic retry could not be started: {result.Error}");
    }

    /// <summary>Runs the real initialization exactly once per <see cref="DownloadManager"/>
    /// instance (the double-checked <c>_initialized</c> flag guarantees that), and - only for
    /// whichever caller was the one to actually perform it - follows up with
    /// <see cref="ResumeAllInterruptedAsync"/> when <see cref="DownloadSettings.AutoResumeDownloadsOnStartup"/>
    /// is on. Every other concurrent caller returns before reaching that point, so it can't run
    /// twice; a caller that returns early may occasionally observe repository state fractionally
    /// ahead of the resume-all pass completing, which is fine - nothing here promises otherwise,
    /// and the UI already reconciles against the repository on its own poll.</summary>
    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized)
                return;
            await _repository.InitializeAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }

        if (_settings.AutoResumeDownloadsOnStartup)
            await ResumeAllInterruptedAsync();
    }
}
