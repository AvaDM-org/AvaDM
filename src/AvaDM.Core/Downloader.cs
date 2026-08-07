using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace AvaDM.Core;

public class Downloader
{
    private const int chunkCount = 5;
    private const int maxRetryAttempts = 5;
    private static readonly TimeSpan baseRetryDelay = TimeSpan.FromSeconds(1);

    // Bounds a single attempt (connect + this attempt's transfer), not the whole chunk.
    // Each retry gets a fresh budget because this sits inside the retry strategy.
    // NB: fixed per-attempt timeout, not a stall/inactivity timeout - a large chunk that
    // trickles in slowly-but-steadily could still hit this. Revisit if that becomes a problem.
    private static readonly TimeSpan perAttemptTimeout = TimeSpan.FromSeconds(30);

    // Built once and reused - ResiliencePipeline is expensive to build, cheap and thread-safe to execute.
    private static readonly ResiliencePipeline ChunkPipeline = BuildChunkPipeline();

    private static ResiliencePipeline BuildChunkPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(IsTransientHttpError)
                    .Handle<IOException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = maxRetryAttempts,
                Delay = baseRetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    Console.WriteLine(
                        "Chunk request failed (attempt {0}), retrying in {1:0.0}s: {2}",
                        args.AttemptNumber + 1,
                        args.RetryDelay.TotalSeconds,
                        args.Outcome.Exception?.Message);
                    return default;
                }
            })
            // Added after retry, so it wraps each individual attempt rather than the whole chunk.
            .AddTimeout(perAttemptTimeout)
            .Build();
    }

    // Only transient failures are worth retrying: connection-level failures (no response at all)
    // and 408/429/5xx. Other 4xx (404, 403, ...) are permanent - retrying just wastes time.
    private static bool IsTransientHttpError(HttpRequestException ex)
    {
        if (ex.StatusCode is null)
            return true;

        return ex.StatusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.InternalServerError => true,
            HttpStatusCode.BadGateway => true,
            HttpStatusCode.ServiceUnavailable => true,
            HttpStatusCode.GatewayTimeout => true,
            _ => false
        };
    }

    public async Task Download(Uri uri, CancellationToken cancellationToken = default)
    {
        var directory = "/home/mazdak/projects/AvaDM/AvaDM.Console/test-dir/";

        using var client = new HttpClient();
        // Polly's per-attempt timeout is the single source of truth for timing out an attempt.
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;

        var msg = new HttpRequestMessage(HttpMethod.Head, uri);
        var headResponse = await client.SendAsync(msg, cancellationToken);

        var filename = Path.GetFileName(uri.LocalPath);
        directory += filename;
        long totalSize = headResponse.Content.Headers.ContentLength
                         ?? throw new InvalidOperationException("Server did not return a Content-Length header.");
        bool supportsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");

        if (!supportsRanges)
            throw new NotSupportedException("Server does not support range requests.");

        using var handle = File.OpenHandle(
            directory,
            mode: FileMode.Create,
            access: FileAccess.ReadWrite,
            share: FileShare.ReadWrite,
            options: FileOptions.Asynchronous,
            preallocationSize: totalSize
        );

        long bytesPerChunk = totalSize / chunkCount;

        for (var i = 0; i < chunkCount; i++)
        {
            long start = i * bytesPerChunk;
            long end = (i == chunkCount - 1) ? totalSize - 1 : start + bytesPerChunk - 1;

            Console.WriteLine("Downloading chunk {0} ({1}-{2})...", i, start, end);
            // Failures that survive retry propagate out of here and fail the whole download -
            // a chunk we can't recover must not be silently skipped, or the output file ends
            // up with a silent hole in it.
            await DownloadChunkAsync(client, handle, uri, i, start, end, cancellationToken);
        }
    }

    private static async Task DownloadChunkAsync(
        HttpClient client,
        SafeFileHandle handle,
        Uri uri,
        int chunkIndex,
        long start,
        long end,
        CancellationToken cancellationToken)
    {
        // Mutable across retries: on a retry we resume from the last byte actually written to
        // disk rather than re-downloading the whole chunk from its original start.
        long currentOffset = start;

        await ChunkPipeline.ExecuteAsync(async ct =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Range = new RangeHeaderValue(currentOffset, end);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, bytesRead), currentOffset, ct);
                currentOffset += bytesRead;
            }
        }, cancellationToken);

        Console.WriteLine("Chunk {0} complete.", chunkIndex);
    }
}
