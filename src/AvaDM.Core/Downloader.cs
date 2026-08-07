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

    private readonly HttpClient _client;
    private readonly ResiliencePipeline _pipeline;

    public Downloader(HttpClient client, ResiliencePipeline pipeline)
    {
        _client = client;
        _pipeline = pipeline;
    }

    /// <summary>Starts a download and returns immediately with a live handle. The download
    /// itself runs in the background; await <c>handle.Completion</c> to wait for it.</summary>
    public DownloadHandle StartDownload(Uri uri, string destinationPath, DownloadOptions? options = null)
    {
        options ??= new DownloadOptions();
        var handle = new DownloadHandle(uri, destinationPath, options);
        handle.Start(ct => RunAsync(uri, destinationPath, options, handle, ct));
        return handle;
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
            // either), so the whole file is re-requested from scratch on each retry.
            handle.Log("Server does not support range requests; falling back to a single sequential download.");
            await DownloadWholeFileAsync(fileHandle, uri, totalSize, handle, cancellationToken);
            return;
        }

        int chunkCount = options.ChunkCount;
        long bytesPerChunk = totalSize / chunkCount;

        var chunkTasks = new Task[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            long start = i * bytesPerChunk;
            long end = (i == chunkCount - 1) ? totalSize - 1 : start + bytesPerChunk - 1;

            chunkTasks[i] = DownloadChunkAsync(fileHandle, uri, start, end, handle, cancellationToken);
        }

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
        long start,
        long end,
        DownloadHandle handle,
        CancellationToken cancellationToken)
    {
        // Mutable across retries: on a retry we resume from the last byte actually written to
        // disk rather than re-downloading the whole chunk from its original start.
        long currentOffset = start;

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
                    handle.AddBytesDownloaded(bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, cancellationToken);
    }

    private async Task DownloadWholeFileAsync(
        SafeFileHandle fileHandle,
        Uri uri,
        long totalSize,
        DownloadHandle handle,
        CancellationToken cancellationToken)
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
                    handle.AddBytesDownloaded(bytesRead);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, cancellationToken);

        handle.Log($"Download complete ({totalSize} bytes).");
    }
}
