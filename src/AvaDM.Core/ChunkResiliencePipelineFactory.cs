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
///
/// Deliberately just a retry policy - no <c>AddTimeout</c> here. A flat per-execution timeout
/// would bound an attempt's total duration, which is wrong for a large chunk (or a whole-file
/// download) that's merely slow-but-steady rather than stalled, and actively harmful for a
/// non-resumable whole-file attempt whose real transfer time exceeds it: every retry there
/// restarts from byte 0, so it would hit the same wall at the same point forever. Instead,
/// <see cref="Downloader"/> runs its own resettable inactivity watchdog inside each attempt and
/// throws <see cref="TimeoutRejectedException"/> - handled below like any other retryable
/// failure - only when an attempt goes genuinely silent for <see cref="DownloadSettings.DefaultInactivityTimeout"/>.
/// </summary>
public static class ChunkResiliencePipelineFactory
{
    public static ResiliencePipeline Create(
        int maxRetryAttempts = 5,
        TimeSpan? baseRetryDelay = null,
        Action<int, TimeSpan, Exception?>? onRetry = null)
    {
        var delay = baseRetryDelay ?? TimeSpan.FromSeconds(1);

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
                    onRetry?.Invoke(args.AttemptNumber + 1, args.RetryDelay, args.Outcome.Exception);
                    return default;
                }
            })
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
