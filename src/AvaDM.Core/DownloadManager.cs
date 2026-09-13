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

    /// <summary>Every handle's "fully finalized" task (persisted its terminal state, removed
    /// itself from <see cref="_activeHandles"/>, and - for a Failed handle - decided on
    /// auto-retry), keyed by download id. Populated synchronously in <see cref="SyncToRepository"/>
    /// the moment a handle is registered, so it's always present the instant
    /// <see cref="GetActiveHandle"/> can return that handle - unlike awaiting the handle's own
    /// <see cref="DownloadHandle.Completion"/> directly, which only waits for the transfer to stop,
    /// not for <see cref="FinalizeDownloadAsync"/> (a separate, unawaited continuation on that same
    /// task) to actually run. A caller that cancels a stale handle and then persists a *different*
    /// state for the same id needs to await this, not just Completion - otherwise the stale
    /// handle's own terminal-state write can land after the caller's, silently reverting it. See
    /// AddDownloadAsync's stale-handle-replacement step.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _pendingFinalizations = new();
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

                // Awaits the stale handle's *entire* finalization, not just its Completion task:
                // Completion finishing only means the transfer stopped, not that
                // FinalizeDownloadAsync (a separate, unawaited continuation on it) has actually
                // written the stale handle's terminal state (Cancelled) to the repository yet.
                // Without this, that write could land after this call's own Running/Queued write
                // for the very same id further down, silently reverting it back to Cancelled -
                // found via manual testing of #25, where routing every Resume through here (rather
                // than resuming a Paused handle in place) made this race hit on every single
                // pause-then-resume instead of only on the rarer resume-a-failed-download path.
                if (_pendingFinalizations.TryGetValue(conflict.ExistingRecord!.Id, out var pendingFinalize))
                {
                    try
                    {
                        await pendingFinalize;
                    }
                    catch
                    {
                        // FinalizeDownloadAsync already catches and logs its own exceptions; this
                        // is just defensive in case that ever changes.
                    }
                }
                else
                {
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
        }

        // This must update the existing row in place (same Id) rather than INSERT a second row,
        // which would trip the UNIQUE(Uri, DestinationPath) constraint and, even if it didn't,
        // would hand back a new Id that orphans a UI row already keyed on the old one.
        // RenameDestination targets a path already confirmed conflict-free above, so it always
        // gets a fresh row.
        var id = conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite
            ? conflict.ExistingRecord!.Id
            : Guid.NewGuid();

        // Persists the row as either Running or Queued, matching whatever TryStartHandleAsync
        // below decides. Queuing under Resume preserves BytesDownloaded/TotalBytes rather than
        // resetting them - the .avadm footer's progress is still genuinely on disk, untouched,
        // since nothing has actually restarted yet. Overwrite already deleted the footer earlier
        // in this method regardless of whether the retry starts immediately or queues, so
        // resetting to 0 there is honest either way.
        Task PersistAsync(DownloadState state)
        {
            if (conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite)
            {
                return state == DownloadState.Queued && resolution is ConflictResolution.Resume
                    ? _repository.UpdateStateAsync(id, state)
                    : _repository.ResetForRestartAsync(id, state, 0);
            }

            return _repository.InsertAsync(id, uri.AbsoluteUri, resolvedPath, state, 0);
        }

        // Only actually starts the transfer (and only then calls Downloader.StartDownload) if
        // there's a free concurrency slot right now; otherwise the row is persisted as Queued and
        // AdmitQueuedDownloadsAsync starts it later once one opens up.
        var handle = await TryStartHandleAsync(id, uri, resolvedPath, options, PersistAsync);

        if (handle is null)
            _queuedOptions[id] = options;
        // else: TryStartHandleAsync already persisted Running, registered the handle in
        // _activeHandles, and wired SyncToRepository, all under the same lock acquisition as the
        // admission check itself - see that method's doc comment for why that has to be one
        // atomic step, not several.

        return new AddDownloadResult(true, id, handle, null);
    }

    private int RunningHandleCount() => _activeHandles.Values.Count(h => h.State == DownloadState.Running);

    /// <summary>Starts a transfer and registers it as active only if there's room under
    /// <see cref="DownloadSettings.DefaultMaxConcurrentDownloads"/> right now, returning
    /// <c>null</c> instead of a handle when there isn't (after persisting the row as
    /// <see cref="DownloadState.Queued"/> via <paramref name="persistAsync"/>). A
    /// <see cref="DownloadState.Paused"/> handle doesn't count against the limit - pausing frees
    /// its slot for a queued download to start, and it only reclaims one for itself the next time
    /// it's resumed.
    ///
    /// The count-check, <paramref name="persistAsync"/> call, <see cref="Downloader.StartDownload"/>
    /// call, and <c>_activeHandles</c> registration all happen under one <see cref="_admissionLock"/>
    /// acquisition, in that order. Two things depend on that:
    /// <list type="bullet">
    /// <item>Registration used to happen afterward, back in the caller, outside the lock. That gap
    /// let a concurrent <see cref="AdmitQueuedDownloadsAsync"/> pass (triggered by some unrelated
    /// pause or completion elsewhere) run in between: it would count this brand-new handle as not
    /// yet running (it wasn't in <c>_activeHandles</c> yet) and see this same download's row still
    /// reading its old state in the repository (that write hadn't happened yet either), and admit
    /// it a second time - starting a second <see cref="DownloadHandle"/> for the same id, both
    /// writing the same <c>.avadm</c> file concurrently.</item>
    /// <item><paramref name="persistAsync"/> is called for Running *before* <see cref="Downloader.StartDownload"/>,
    /// not after. <c>StartDownload</c> returns a "hot" handle that's already transferring - for a
    /// download that fails (or completes) near-instantly, its own terminal-state write from
    /// <see cref="FinalizeDownloadAsync"/> could otherwise race the "just started, mark Running"
    /// write for the exact same id, with no guarantee which one actually lands last. Persisting
    /// Running strictly before the handle exists at all means that write always happens-before
    /// anything the handle itself could ever write, by construction.</item>
    /// </list>
    /// Together, these are how a download ended up marked Completed while its actual bytes on
    /// disk were incomplete/corrupted and un-resumable (first bullet), and separately how a
    /// download that failed immediately could get stuck permanently showing Running (second
    /// bullet) - both found via manual testing of #25.</summary>
    private async Task<DownloadHandle?> TryStartHandleAsync(
        Guid id, Uri uri, string resolvedPath, DownloadOptions? options, Func<DownloadState, Task> persistAsync)
    {
        await _admissionLock.WaitAsync();
        try
        {
            if (RunningHandleCount() >= _settings.DefaultMaxConcurrentDownloads)
            {
                await persistAsync(DownloadState.Queued);
                return null;
            }

            await persistAsync(DownloadState.Running);
            var handle = _downloader.StartDownload(uri, resolvedPath, options);
            _activeHandles[id] = handle;
            SyncToRepository(id, handle);
            return handle;
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

                // Defensive: never start a second handle for an id that already has a live one.
                // _activeHandles is the true "what's actually running right now" source of truth;
                // the repository is a lagging mirror of it and can briefly still say Queued for a
                // download another in-flight call just started (see TryStartHandleAsync's doc
                // comment for the exact race this guards against).
                if (_activeHandles.ContainsKey(record.Id))
                    continue;

                _queuedOptions.TryRemove(record.Id, out var options);
                // Persisted before starting the (immediately "hot") handle, not after - see
                // TryStartHandleAsync's doc comment for why a fast-failing/fast-completing
                // download could otherwise race this write with its own terminal-state one.
                await _repository.UpdateStateAsync(record.Id, DownloadState.Running);
                var handle = _downloader.StartDownload(new Uri(record.Uri), record.DestinationPath, options);
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

    /// <summary>Re-queues (not merely pauses) however many currently-Running downloads are
    /// needed to bring the running count back to (or under)
    /// <see cref="DownloadSettings.DefaultMaxConcurrentDownloads"/> - the counterpart to
    /// <see cref="AdmitQueuedDownloadsAsync"/> for when the limit is *lowered* below what's
    /// already running, which that method alone never addresses (it only ever admits more, never
    /// backs off existing ones). A no-op if nothing is currently over the limit.
    ///
    /// Deliberately re-queues rather than pauses: a paused download only starts again once the
    /// user manually resumes it, whereas a download bumped by a *lowered limit* should pick back
    /// up on its own the moment room exists again, exactly like anything else waiting its turn -
    /// the user didn't ask for it to stop, the limit just changed.
    ///
    /// When there's more to bump than room to cut, the downloads with the highest
    /// <c>QueueOrder</c> (i.e. added or re-queued most recently) go first, leaving whichever have
    /// been running the longest untouched - an arbitrary but deterministic and stable tie-break,
    /// consistent with the FIFO ordering the queue already uses everywhere else.</summary>
    public async Task EnforceConcurrencyLimitAsync()
    {
        await EnsureInitializedAsync();
        await _admissionLock.WaitAsync();
        try
        {
            var limit = _settings.DefaultMaxConcurrentDownloads;
            var runningHandles = _activeHandles
                .Where(kv => kv.Value.State == DownloadState.Running)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var excess = runningHandles.Count - limit;
            if (excess <= 0)
                return;

            var toRequeue = (await _repository.GetAllAsync())
                .Where(r => runningHandles.ContainsKey(r.Id))
                .OrderByDescending(r => r.QueueOrder)
                .Take(excess)
                .ToList();

            foreach (var record in toRequeue)
            {
                var handle = runningHandles[record.Id];
                handle.Cancel();

                // Awaits the handle's full finalization, not just its Completion task - same
                // reasoning as AddDownloadAsync's stale-handle-replacement step: without this, the
                // handle's own terminal-state write (Cancelled) could land after this method's own
                // Queued write below and silently revert it.
                if (_pendingFinalizations.TryGetValue(record.Id, out var pendingFinalize))
                {
                    try
                    {
                        await pendingFinalize;
                    }
                    catch
                    {
                        // FinalizeDownloadAsync already catches and logs its own exceptions.
                    }
                }

                // UpdateStateAsync only touches State - the real BytesDownloaded/TotalBytes the
                // cancelled handle just persisted (via its own finalize, awaited above) stays
                // intact, so this download resumes from exactly where it left off once re-admitted.
                await _repository.UpdateStateAsync(record.Id, DownloadState.Queued);
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

    /// <summary>Holds a still-<see cref="DownloadState.Queued"/> download back from admission
    /// until explicitly resumed - the queued equivalent of pausing a running download. No handle
    /// exists yet, so there's nothing to call <see cref="DownloadHandle.Pause"/> on; this is a
    /// plain state flip to <see cref="DownloadState.QueuedPaused"/>, which
    /// <see cref="AdmitQueuedDownloadsAsync"/>'s scan simply never selects. A no-op (returns
    /// <c>false</c>) if the row isn't currently <see cref="DownloadState.Queued"/>.</summary>
    public async Task<bool> PauseQueuedDownloadAsync(Guid id)
    {
        await EnsureInitializedAsync();

        var record = await _repository.GetByIdAsync(id);
        if (record is null || record.State != DownloadState.Queued)
            return false;

        await _repository.UpdateStateAsync(id, DownloadState.QueuedPaused);
        return true;
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
        // terminal state is always recorded even if the last throttled write is stale. The
        // ContinueWith call itself - not its completion - is what has to happen synchronously
        // here: it hands back a Task immediately, which is stored in _pendingFinalizations before
        // this method returns, so that dictionary entry can never race a caller looking it up via
        // GetActiveHandle right after (see that field's doc comment).
        var finalizeTask = handle.Completion.ContinueWith(async t =>
        {
            // Reading Exception is what marks a faulted antecedent "observed" - without it, the
            // fault sits unobserved until the GC finalizes the Task, and the runtime rethrows it
            // on the finalizer thread, which the global TaskScheduler.UnobservedTaskException
            // handler then logs as an alarming "Unobserved task exception" even though the
            // failure is already handled cleanly below via handle.State.
            if (t.Exception is not null)
                handle.Log($"Download failed: {t.Exception.GetBaseException().Message}");

            // FinalizeDownloadAsync catches and logs its own exceptions - it won't fault this.
            await FinalizeDownloadAsync(id, handle);
        }).Unwrap();

        _pendingFinalizations[id] = finalizeTask;
        _ = finalizeTask.ContinueWith(_ =>
            // Conditional remove: don't delete a newer entry a resumed download for this same id
            // has since installed - same reasoning as _activeHandles' own conditional remove in
            // FinalizeDownloadAsync.
            ((ICollection<KeyValuePair<Guid, Task>>)_pendingFinalizations).Remove(new(id, finalizeTask)));
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

        // A terminal handle (Completed/Failed/Cancelled) always frees its slot. TryAutoRetryAsync
        // runs first, not AdmitQueuedDownloadsAsync - it goes through AddDownloadAsync just like a
        // manual resume, which is itself concurrency-aware, so a retry that finds the slot already
        // taken by something else still queues correctly either way. Running it first instead of
        // firing AdmitQueuedDownloadsAsync beforehand avoids the two contending for _admissionLock
        // on every single attempt of a fast retry loop (AdmitQueuedDownloadsAsync's own repository
        // round trip would otherwise still be holding the lock when the retry's AddDownloadAsync
        // tries to acquire it) - measurable added latency per attempt that made
        // FailedDownload_AutoRetries_UpToConfiguredLimitThenStops and
        // ResumeDownloadAsync_ManualCall_ResetsAutoRetryCounter's fixed timing budgets flaky. The
        // trade-off: a retry can now win a just-freed slot ahead of something else already queued,
        // rather than always queuing behind it - a minor fairness question, not a correctness one.
        if (handle.State == DownloadState.Failed)
            await TryAutoRetryAsync(id, handle);

        _ = AdmitQueuedDownloadsAsync();
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
