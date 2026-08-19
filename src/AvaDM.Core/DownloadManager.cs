using System.Collections.Concurrent;
using System.Diagnostics;
using Polly;

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

    public DownloadManager(HttpClient client, ResiliencePipeline pipeline, DownloadSettings settings)
    {
        _downloader = new Downloader(client, pipeline, settings);
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
        var id = Guid.NewGuid();
        await _repository.InsertAsync(id, uri.AbsoluteUri, resolvedPath, DownloadState.Running, handle.TotalBytes);
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
        handle.Completion.ContinueWith(_ =>
        {
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
