using System.Buffers;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using Polly;

namespace AvaDM.Core;

/// <summary>
/// Downloads files over HTTP, splitting into concurrent Range-request chunks when the server
/// supports it. Holds no per-download mutable state - every call to <see cref="StartDownload"/>
/// produces an independent <see cref="DownloadHandle"/>, so one <see cref="Downloader"/> (and
/// the <see cref="HttpClient"/>/pipeline it wraps) can safely drive any number of downloads at
/// once, each internally running its own parallel chunks.
///
/// Every download writes to a <c>&lt;destination&gt;.avadm</c> working file: payload bytes at
/// their natural <c>[0, TotalBytes)</c> offsets, followed by a self-describing binary footer
/// (see <see cref="DownloadFooter"/>) that a later process can use to resume without any other
/// state. A previous run's sidecar is detected and validated against a fresh HEAD before it's
/// trusted; anything that doesn't check out falls back to starting over rather than throwing.
/// </summary>
public class Downloader
{
    private const int BufferSize = 81920;
    private const string FallbackFileName = "download";
    private const string WorkingFileSuffix = ".avadm";
    private const int FooterLengthFieldSize = 8;
    private static readonly TimeSpan FooterCheckpointInterval = TimeSpan.FromSeconds(5);

    private readonly HttpClient _client;
    private readonly DownloadSettings _settings;

    public Downloader(HttpClient client, DownloadSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public DownloadHandle StartDownload(Uri uri, string? destinationPath, DownloadOptions? options = null)
    {
        options ??= new DownloadOptions();
        var mergedOptions = options with
        {
            ChunkCount = options.ChunkCount ?? _settings.DefaultChunkCount,
            InitialSpeedLimitBytesPerSecond = options.InitialSpeedLimitBytesPerSecond ?? _settings.DefaultSpeedLimitBytesPerSecond,
        };
        var resolvedPath = ResolveDestinationPath(uri, destinationPath);
        var handle = new DownloadHandle(uri, resolvedPath, mergedOptions);
        handle.Start(ct => RunAsync(uri, resolvedPath, mergedOptions, handle, ct));
        return handle;
    }

    internal string ResolveDestinationPath(Uri uri, string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            return Path.Combine(_settings.DefaultDownloadDirectory, FileNameFromUri(uri));

        if (LooksLikeDirectory(destinationPath))
            return Path.Combine(destinationPath, FileNameFromUri(uri));

        return destinationPath;
    }

    /// <summary>Decides whether a caller-supplied destination means "put the URL's filename inside
    /// this directory" rather than "this exact path is the file". True for a path that already
    /// exists as a directory or ends in a separator (explicit either way), and also for a
    /// not-yet-existing path with no file extension - e.g. <c>./tests</c> meant as a folder to be
    /// created, which is the common case a caller runs into before the directory exists. A path
    /// that already exists as a file (e.g. resuming into a previously finalized destination with
    /// no extension) or a working <c>.avadm</c> sidecar for it is still treated as a literal file,
    /// so an in-progress resume doesn't flip interpretation mid-download; an explicit extension
    /// (as <c>--rename</c> targets typically have) is likewise always treated as a literal file.</summary>
    private static bool LooksLikeDirectory(string path) =>
        Directory.Exists(path) ||
        path.EndsWith(Path.DirectorySeparatorChar) ||
        path.EndsWith(Path.AltDirectorySeparatorChar) ||
        (string.IsNullOrEmpty(Path.GetExtension(path)) &&
         !File.Exists(path) &&
         !File.Exists(path + WorkingFileSuffix));

    private static string FileNameFromUri(Uri uri)
    {
        var name = Path.GetFileName(uri.LocalPath);
        return string.IsNullOrEmpty(name) ? FallbackFileName : name;
    }

    private async Task RunAsync(
        Uri uri,
        string destinationPath,
        DownloadOptions options,
        DownloadHandle handle,
        CancellationToken cancellationToken)
    {
        // Built fresh per download (not shared/cached) so a settings change picks up on the next
        // download started, the same way DefaultChunkCount and DefaultSpeedLimitBytesPerSecond do.
        // Cheap relative to the download itself, and onRetry needs to close over this handle.
        var pipeline = ChunkResiliencePipelineFactory.Create(
            _settings.DefaultMaxRetryAttempts,
            _settings.DefaultRetryBaseDelay,
            _settings.DefaultPerAttemptTimeout,
            onRetry: (attempt, delay, ex) =>
                handle.LogDiagnostic($"Request failed (attempt {attempt}), retrying in {delay.TotalSeconds:0.0}s: {ex?.Message}"));

        var msg = new HttpRequestMessage(HttpMethod.Head, uri);
        var headResponse = await _client.SendAsync(msg, cancellationToken);

        long totalSize = headResponse.Content.Headers.ContentLength
                         ?? throw new InvalidOperationException("Server did not return a Content-Length header.");
        bool supportsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");
        handle.TotalBytes = totalSize;

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var workingPath = destinationPath + WorkingFileSuffix;

        if (!supportsRanges)
        {
            // A whole-file download re-requests the entire body from scratch on every attempt,
            // so it can never resume - any prior .avadm for this destination is simply replaced.
            handle.Log("Server does not support range requests; falling back to a single sequential download.");
            var footerSize = DownloadFooter.ComputeSize(uri, 1);
            var fileHandle = OpenFreshWorkingFile(workingPath, totalSize + footerSize);
            try
            {
                handle.InitializeChunks([(0, totalSize - 1)]);
                await RunWithFooterCheckpointingAsync(
                    fileHandle, uri, totalSize, resumable: false, handle,
                    ct => DownloadWholeFileAsync(fileHandle, uri, totalSize, handle, pipeline, ct),
                    cancellationToken);
            }
            finally
            {
                fileHandle.Dispose();
            }

            await FinalizeAsync(workingPath, destinationPath, totalSize);
            return;
        }

        var resumeFooter = await TryReadResumeFooterAsync(workingPath, uri, totalSize, handle, cancellationToken);

        SafeFileHandle rangedFileHandle;
        (long Start, long End)[] ranges;

        if (resumeFooter is not null)
        {
            var candidateRanges = resumeFooter.Chunks.Select(c => (c.Start, c.End)).ToArray();
            var footerSize = DownloadFooter.ComputeSize(uri, candidateRanges.Length);
            var reopened = TryOpenResumableWorkingFile(workingPath, totalSize + footerSize, handle);
            if (reopened is not null)
            {
                rangedFileHandle = reopened;
                ranges = candidateRanges;
                handle.InitializeChunksFromFooter(resumeFooter.Chunks);
                var alreadyDownloaded = resumeFooter.Chunks.Sum(c => c.BytesDownloaded);
                handle.Log($"Resuming '{destinationPath}': {alreadyDownloaded} of {totalSize} bytes already on disk.");
            }
            else
            {
                (rangedFileHandle, ranges) = OpenFreshChunkedWorkingFile(uri, workingPath, totalSize, options.ChunkCount!.Value);
                handle.InitializeChunks(ranges);
            }
        }
        else
        {
            (rangedFileHandle, ranges) = OpenFreshChunkedWorkingFile(uri, workingPath, totalSize, options.ChunkCount!.Value);
            handle.InitializeChunks(ranges);
        }

        try
        {
            var chunkTasks = new Task[ranges.Length];
            for (var i = 0; i < ranges.Length; i++)
                chunkTasks[i] = DownloadChunkAsync(rangedFileHandle, uri, i, ranges[i].Start, ranges[i].End, handle, pipeline, cancellationToken);

            await RunWithFooterCheckpointingAsync(
                rangedFileHandle, uri, totalSize, resumable: true, handle,
                _ => Task.WhenAll(chunkTasks),
                cancellationToken);
        }
        finally
        {
            rangedFileHandle.Dispose();
        }

        await FinalizeAsync(workingPath, destinationPath, totalSize);
    }

    private static (long Start, long End)[] ComputeRanges(long totalSize, int chunkCount)
    {
        long bytesPerChunk = totalSize / chunkCount;
        var ranges = new (long Start, long End)[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            long start = i * bytesPerChunk;
            long end = (i == chunkCount - 1) ? totalSize - 1 : start + bytesPerChunk - 1;
            ranges[i] = (start, end);
        }
        return ranges;
    }

    private static (SafeFileHandle FileHandle, (long Start, long End)[] Ranges) OpenFreshChunkedWorkingFile(
        Uri uri, string workingPath, long totalSize, int chunkCount)
    {
        var ranges = ComputeRanges(totalSize, chunkCount);
        var footerSize = DownloadFooter.ComputeSize(uri, ranges.Length);
        var fileHandle = OpenFreshWorkingFile(workingPath, totalSize + footerSize);
        return (fileHandle, ranges);
    }

    /// <summary>Creates (or replaces) the working file at its final size - payload region plus
    /// the footer that will be checkpointed after it. Used both for a first attempt and whenever
    /// an existing <c>.avadm</c> turned out to be unusable for resume.</summary>
    private static SafeFileHandle OpenFreshWorkingFile(string workingPath, long requiredLength)
    {
        return File.OpenHandle(
            workingPath,
            mode: FileMode.Create,
            access: FileAccess.ReadWrite,
            share: FileShare.ReadWrite,
            options: FileOptions.Asynchronous,
            preallocationSize: requiredLength);
    }

    /// <summary>Reopens an existing <c>.avadm</c> whose footer already passed validation, for
    /// continued writing. Returns <c>null</c> (after logging and disposing) if the file's actual
    /// length doesn't match what the footer implies - corruption we can detect cheaply without
    /// reading the whole payload.</summary>
    private static SafeFileHandle? TryOpenResumableWorkingFile(string workingPath, long requiredLength, DownloadHandle handle)
    {
        var fileHandle = File.OpenHandle(
            workingPath,
            mode: FileMode.Open,
            access: FileAccess.ReadWrite,
            share: FileShare.ReadWrite,
            options: FileOptions.Asynchronous);

        if (RandomAccess.GetLength(fileHandle) != requiredLength)
        {
            handle.Log("Existing resume data has an unexpected file size; starting this download over from scratch.");
            fileHandle.Dispose();
            return null;
        }

        return fileHandle;
    }

    /// <summary>Reads and validates a previous run's footer from <paramref name="workingPath"/>,
    /// if one exists. Returns <c>null</c> (after logging) on any missing file, parse failure, or
    /// mismatch against the fresh HEAD (<paramref name="totalSize"/>/<paramref name="uri"/>) or
    /// a cleared resumable flag - callers treat that uniformly as "start fresh", never throw.</summary>
    private static async Task<DownloadFooterData?> TryReadResumeFooterAsync(
        string workingPath, Uri uri, long totalSize, DownloadHandle handle, CancellationToken cancellationToken)
    {
        if (!File.Exists(workingPath))
            return null;

        try
        {
            await using var stream = new FileStream(workingPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < FooterLengthFieldSize)
                return null;

            var lengthBuffer = new byte[FooterLengthFieldSize];
            stream.Seek(-FooterLengthFieldSize, SeekOrigin.End);
            await stream.ReadExactlyAsync(lengthBuffer, cancellationToken);
            var footerLength = BinaryPrimitives.ReadInt64BigEndian(lengthBuffer);

            if (footerLength <= 0 || footerLength > stream.Length)
                return null;

            var footerBuffer = new byte[footerLength];
            stream.Seek(-footerLength, SeekOrigin.End);
            await stream.ReadExactlyAsync(footerBuffer, cancellationToken);

            var footer = DownloadFooter.Deserialize(footerBuffer);

            if (!footer.Resumable || footer.TotalBytes != totalSize || footer.SourceUri.AbsoluteUri != uri.AbsoluteUri)
            {
                handle.Log("Existing resume data does not match this download (URL or size changed); starting over.");
                return null;
            }

            return footer;
        }
        catch (Exception ex) when (ex is FormatException or IOException or EndOfStreamException or ArgumentException)
        {
            handle.Log($"Could not read resume data from '{workingPath}': {ex.Message}. Starting over.");
            return null;
        }
    }

    /// <summary>Runs <paramref name="runChunks"/> alongside a background footer-checkpoint loop,
    /// so the sidecar reflects real progress every few seconds without any chunk task writing to
    /// the footer region itself. The loop keeps running across pause and only stops once
    /// <paramref name="runChunks"/> finishes (success, failure, or cancellation), always doing one
    /// last write first so the footer is never more than one interval stale.</summary>
    private static async Task RunWithFooterCheckpointingAsync(
        SafeFileHandle fileHandle,
        Uri uri,
        long totalSize,
        bool resumable,
        DownloadHandle handle,
        Func<CancellationToken, Task> runChunks,
        CancellationToken cancellationToken)
    {
        using var footerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var footerLoop = RunFooterCheckpointLoopAsync(fileHandle, uri, totalSize, resumable, handle, footerCts.Token);
        try
        {
            await runChunks(cancellationToken);
        }
        finally
        {
            footerCts.Cancel();
            await footerLoop;
        }
    }

    private static async Task RunFooterCheckpointLoopAsync(
        SafeFileHandle fileHandle, Uri uri, long totalSize, bool resumable, DownloadHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(FooterCheckpointInterval, cancellationToken);
                await WriteFooterAsync(fileHandle, uri, totalSize, resumable, handle, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelled once the chunk downloads stop, for any reason. The final write
            // below (always run, success or not) captures wherever things actually landed.
        }
        finally
        {
            await WriteFooterAsync(fileHandle, uri, totalSize, resumable, handle, CancellationToken.None);
        }
    }

    private static async Task WriteFooterAsync(
        SafeFileHandle fileHandle, Uri uri, long totalSize, bool resumable, DownloadHandle handle, CancellationToken cancellationToken)
    {
        var chunks = handle.Chunks
            .Select(c => new ChunkFooterData(c.StartByte, c.EndByte, c.BytesDownloaded, c.Status))
            .ToArray();
        var footer = DownloadFooter.Serialize(new DownloadFooterData(uri, totalSize, resumable, chunks));
        await RandomAccess.WriteAsync(fileHandle, footer, totalSize, cancellationToken);
    }

    /// <summary>Closes off a successful download: truncates away the footer (the shared handle
    /// must be closed first - there's no <c>RandomAccess.SetLength</c>) and renames the working
    /// file onto its final destination.</summary>
    private static void FinalizeAsync_Sync(string workingPath, string destinationPath, long totalSize)
    {
        using (var fs = new FileStream(workingPath, FileMode.Open, FileAccess.Write))
            fs.SetLength(totalSize);
        File.Move(workingPath, destinationPath, overwrite: true);
    }

    private static Task FinalizeAsync(string workingPath, string destinationPath, long totalSize)
    {
        FinalizeAsync_Sync(workingPath, destinationPath, totalSize);
        return Task.CompletedTask;
    }

    private async Task DownloadChunkAsync(
        SafeFileHandle fileHandle,
        Uri uri,
        int chunkIndex,
        long start,
        long end,
        DownloadHandle handle,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken)
    {
        var chunkSize = end - start + 1;
        var alreadyDownloaded = handle.Chunks[chunkIndex].BytesDownloaded;
        if (alreadyDownloaded >= chunkSize)
        {
            // Fully downloaded in a previous run - no HTTP request needed at all.
            handle.SetChunkStatus(chunkIndex, ChunkStatus.Completed);
            return;
        }

        long currentOffset = start + alreadyDownloaded;

        handle.SetChunkStatus(chunkIndex, ChunkStatus.Downloading);
        try
        {
            // The wait for resume deliberately sits outside pipeline.ExecuteAsync, on the
            // download's own (not time-boxed) cancellationToken - never on the per-attempt `ct`
            // Polly hands the delegate below. That `ct` is cancelled by AddTimeout on a fixed
            // wall clock regardless of what's being awaited, so a pause held inside it would
            // eventually trip TimeoutRejectedException, get retried, and immediately hit the
            // still-paused wait again - a fast retry loop that burns every attempt while paused.
            // Instead, a pause request ends the current attempt cleanly (IsPaused check below,
            // no exception raised) and the next attempt's Range request just continues from
            // wherever currentOffset landed, once resumed.
            while (currentOffset <= end)
            {
                await handle.PauseTokenSource.WaitWhilePausedAsync(cancellationToken);

                await pipeline.ExecuteAsync(async ct =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    req.Headers.Range = new RangeHeaderValue(currentOffset, end);

                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);

                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        while (!handle.PauseTokenSource.IsPaused)
                        {
                            var bytesRead = await stream.ReadAsync(buffer, ct);
                            if (bytesRead == 0)
                                break;

                            await handle.SpeedLimiter.WaitForTokensAsync(bytesRead, ct);

                            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), currentOffset, ct);
                            currentOffset += bytesRead;
                            handle.AddChunkBytesDownloaded(chunkIndex, bytesRead);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }, cancellationToken);
            }

            handle.SetChunkStatus(chunkIndex, ChunkStatus.Completed);
        }
        catch
        {
            handle.SetChunkStatus(chunkIndex, ChunkStatus.Failed);
            throw;
        }
    }

    private async Task DownloadWholeFileAsync(
        SafeFileHandle fileHandle,
        Uri uri,
        long totalSize,
        DownloadHandle handle,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken)
    {
        const int chunkIndex = 0;

        handle.SetChunkStatus(chunkIndex, ChunkStatus.Downloading);
        try
        {
            // See the matching comment in DownloadChunkAsync: the pause wait must sit outside
            // pipeline.ExecuteAsync, on the download's own cancellationToken, never on the
            // per-attempt `ct`, or a pause outlasting the per-attempt timeout trips
            // TimeoutRejectedException, gets retried, and immediately re-hits the still-paused
            // wait - a fast retry loop that burns every attempt while paused. This path has no
            // Range support, so unlike a chunk, an attempt restarted after a pause always
            // re-requests the whole body from byte 0; SetChunkBytesDownloaded rolls back whatever
            // the abandoned attempt had credited so progress doesn't run ahead of the file.
            var completed = false;
            while (!completed)
            {
                await handle.PauseTokenSource.WaitWhilePausedAsync(cancellationToken);
                handle.SetChunkBytesDownloaded(chunkIndex, 0);

                await pipeline.ExecuteAsync(async ct =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, uri);

                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);

                    long offset = 0;
                    var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try
                    {
                        while (!handle.PauseTokenSource.IsPaused)
                        {
                            var bytesRead = await stream.ReadAsync(buffer, ct);
                            if (bytesRead == 0)
                            {
                                completed = true;
                                break;
                            }

                            await handle.SpeedLimiter.WaitForTokensAsync(bytesRead, ct);

                            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), offset, ct);
                            offset += bytesRead;
                            handle.AddChunkBytesDownloaded(chunkIndex, bytesRead);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }, cancellationToken);
            }

            handle.SetChunkStatus(chunkIndex, ChunkStatus.Completed);
        }
        catch
        {
            handle.SetChunkStatus(chunkIndex, ChunkStatus.Failed);
            throw;
        }

        handle.Log($"Download complete ({totalSize} bytes).");
    }
}
