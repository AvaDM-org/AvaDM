using System.Buffers;
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
/// </summary>
public class Downloader
{
    private const int BufferSize = 81920;
    private const string FallbackFileName = "download";

    private readonly HttpClient _client;
    private readonly ResiliencePipeline _pipeline;
    private readonly DownloadSettings _settings;

    /// <summary>Creates a downloader. <paramref name="settings"/> is held by reference and read
    /// on every <see cref="StartDownload"/> call to resolve paths and fill in
    /// <see cref="DownloadOptions"/> defaults - callers (console, UI) mutate it directly to
    /// change defaults for downloads started afterward.</summary>
    public Downloader(HttpClient client, ResiliencePipeline pipeline, DownloadSettings settings)
    {
        _client = client;
        _pipeline = pipeline;
        _settings = settings;
    }

    /// <summary>Starts a download and returns immediately with a live handle. The download
    /// itself runs in the background; await <c>handle.Completion</c> to wait for it.</summary>
    /// <param name="destinationPath">Where to save the file. Pass <c>null</c>/empty to save
    /// under <see cref="DownloadSettings.DefaultDownloadDirectory"/> using a filename derived
    /// from the URL, or pass an existing directory (or a path ending in a directory separator)
    /// to save under it with a filename derived from the URL. Anything else is used as the
    /// exact file path.</param>
    /// <param name="options">Per-download options. Any <c>null</c> field (e.g.
    /// <see cref="DownloadOptions.ChunkCount"/>) is filled in from <see cref="DownloadSettings"/>
    /// before the download starts.</param>
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

    /// <summary>Turns a possibly-missing or possibly-directory-only destination into a concrete
    /// file path. See <see cref="StartDownload"/> for the exact rules.</summary>
    internal string ResolveDestinationPath(Uri uri, string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            return Path.Combine(_settings.DefaultDownloadDirectory, FileNameFromUri(uri));

        if (Directory.Exists(destinationPath) ||
            destinationPath.EndsWith(Path.DirectorySeparatorChar) ||
            destinationPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return Path.Combine(destinationPath, FileNameFromUri(uri));
        }

        return destinationPath;
    }

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
        var msg = new HttpRequestMessage(HttpMethod.Head, uri);
        var headResponse = await _client.SendAsync(msg, cancellationToken);

        long totalSize = headResponse.Content.Headers.ContentLength
                         ?? throw new InvalidOperationException("Server did not return a Content-Length header.");
        bool supportsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");
        handle.TotalBytes = totalSize;

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var fileHandle = File.OpenHandle(
            destinationPath,
            mode: FileMode.Create,
            access: FileAccess.ReadWrite,
            share: FileShare.ReadWrite,
            options: FileOptions.Asynchronous,
            preallocationSize: totalSize
        );

        if (!supportsRanges)
        {
            // No Range support -> can't split into concurrent chunks, and a failed attempt
            // can't resume from where it left off (the server won't honor Range on retry
            // either), so the whole file is re-requested from scratch on each retry. Modeled
            // as a single chunk spanning the whole file so a UI sees a consistent chunk shape
            // regardless of which path ran.
            handle.Log("Server does not support range requests; falling back to a single sequential download.");
            handle.InitializeChunks([(0, totalSize - 1)]);
            await DownloadWholeFileAsync(fileHandle, uri, totalSize, handle, cancellationToken);
            return;
        }

        // Non-null: StartDownload already merged this against DownloadSettings.DefaultChunkCount.
        int chunkCount = options.ChunkCount!.Value;
        long bytesPerChunk = totalSize / chunkCount;

        var ranges = new (long Start, long End)[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            long start = i * bytesPerChunk;
            long end = (i == chunkCount - 1) ? totalSize - 1 : start + bytesPerChunk - 1;
            ranges[i] = (start, end);
        }
        handle.InitializeChunks(ranges);

        var chunkTasks = new Task[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            chunkTasks[i] = DownloadChunkAsync(fileHandle, uri, i, ranges[i].Start, ranges[i].End, handle, cancellationToken);

        // Chunks write to disjoint byte ranges of the same file handle concurrently - safe
        // because RandomAccess.WriteAsync takes an explicit offset per call (no shared cursor).
        // A chunk failure that survives its own retries propagates out here and fails the
        // whole download - a chunk we can't recover must not be silently skipped, or the
        // output file ends up with a silent hole in it.
        await Task.WhenAll(chunkTasks);
    }

    private async Task DownloadChunkAsync(
        SafeFileHandle fileHandle,
        Uri uri,
        int chunkIndex,
        long start,
        long end,
        DownloadHandle handle,
        CancellationToken cancellationToken)
    {
        // Mutable across retries: on a retry we resume from the last byte actually written to
        // disk rather than re-downloading the whole chunk from its original start.
        long currentOffset = start;

        handle.SetChunkStatus(chunkIndex, ChunkStatus.Downloading);
        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Range = new RangeHeaderValue(currentOffset, end);

                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);

                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        await handle.PauseTokenSource.WaitWhilePausedAsync(ct);

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
        CancellationToken cancellationToken)
    {
        // Represented as chunk 0 (the single entry handle.InitializeChunks was called with by
        // the caller) so this path reports progress through the same per-chunk surface as the
        // concurrent-chunks path.
        const int chunkIndex = 0;

        handle.SetChunkStatus(chunkIndex, ChunkStatus.Downloading);
        try
        {
            await _pipeline.ExecuteAsync(async ct =>
            {
                // No Range header: every attempt (including retries) re-requests the full body
                // from byte 0 and overwrites what was previously written.
                var req = new HttpRequestMessage(HttpMethod.Get, uri);

                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);

                long offset = 0;
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        await handle.PauseTokenSource.WaitWhilePausedAsync(ct);

                        var bytesRead = await stream.ReadAsync(buffer, ct);
                        if (bytesRead == 0)
                            break;

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
