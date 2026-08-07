using System.Net;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace AvaDM.Core;

/// <summary>
/// Builds the Polly resilience pipeline used to wrap a single chunk (or whole-file) download
/// attempt. Callers own the resulting <see cref="ResiliencePipeline"/> - build once and reuse
/// across every <see cref="Downloader"/> call, it's expensive to build and cheap/thread-safe
/// to execute.
/// </summary>
public static class ChunkResiliencePipelineFactory
{
    public static ResiliencePipeline Create(
        int maxRetryAttempts = 5,
        TimeSpan? baseRetryDelay = null,
        TimeSpan? perAttemptTimeout = null)
    {
        var delay = baseRetryDelay ?? TimeSpan.FromSeconds(1);
        // Bounds a single attempt (connect + this attempt's transfer), not the whole chunk.
        // Each retry gets a fresh budget because this sits inside the retry strategy.
        // NB: fixed per-attempt timeout, not a stall/inactivity timeout - a large chunk that
        // trickles in slowly-but-steadily could still hit this. Revisit if that becomes a problem.
        var timeout = perAttemptTimeout ?? TimeSpan.FromSeconds(30);

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(IsTransientHttpError)
                    .Handle<IOException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = maxRetryAttempts,
                Delay = delay,
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
            .AddTimeout(timeout)
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
}
