using System.Collections.Concurrent;
using System.Diagnostics;

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
    private readonly ConcurrentDictionary<Guid, DownloadHandle> _activeHandles = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public DownloadManager(HttpClient client, DownloadSettings settings)
    {
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

        var handle = _downloader.StartDownload(uri, resolvedPath, options);

        // Resume/Overwrite restart the *same* (Uri, DestinationPath) row that CheckConflictAsync
        // already found - e.g. paused, app closed, reopened, resumed. That row is still in the
        // table, so this must update it in place (same Id) rather than INSERT a second row, which
        // would trip the UNIQUE(Uri, DestinationPath) constraint and, even if it didn't, would
        // hand back a new Id that orphans a UI row already keyed on the old one. RenameDestination
        // targets a path already confirmed conflict-free above, so it always gets a fresh row.
        Guid id;
        if (conflict.HasConflict && resolution is ConflictResolution.Resume or ConflictResolution.Overwrite)
        {
            id = conflict.ExistingRecord!.Id;
            await _repository.ResetForRestartAsync(id, DownloadState.Running, handle.TotalBytes);
        }
        else
        {
            id = Guid.NewGuid();
            await _repository.InsertAsync(id, uri.AbsoluteUri, resolvedPath, DownloadState.Running, handle.TotalBytes);
        }
        _activeHandles[id] = handle;
        SyncToRepository(id, handle);

        return new AddDownloadResult(true, id, handle, null);
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
    /// after an app restart): looks up its record and re-adds it with
    /// <see cref="ConflictResolution.Resume"/>, which falls through to <see cref="Downloader"/>'s
    /// existing <c>.avadm</c>-footer resume logic - no separate "rehydration" code path needed.</summary>
    public async Task<AddDownloadResult> ResumeDownloadAsync(Guid id)
    {
        await EnsureInitializedAsync();

        var record = await _repository.GetByIdAsync(id);
        if (record is null)
            return new AddDownloadResult(false, null, null, null, "No download found with that id.");

        return await AddDownloadAsync(new Uri(record.Uri), record.DestinationPath, null, new ConflictResolution.Resume());
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
            _activeHandles.TryRemove(id, out _);
        }
    }

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
    }
}
