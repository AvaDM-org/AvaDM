using System.Collections.Concurrent;
using AvaDM.Core;
using Xunit;

namespace AvaDM.Core.Tests;

public sealed class DownloadHandleTests
{
    /// <summary>Regression test for #10: concurrent chunk tasks calling
    /// <see cref="DownloadHandle.AddChunkBytesDownloaded"/> at the same time used to be able to
    /// race each other into <see cref="DownloadHandle.ProgressChanged"/>, delivering a smaller,
    /// stale byte total after a larger one had already gone out - visible in the UI as the
    /// progress bar jumping back. <see cref="DownloadHandle.ReportProgress"/> now serializes the
    /// snapshot-and-invoke under a lock, so the sequence of reported totals must never decrease.
    /// A slow subscriber deliberately widens the window between snapshotting
    /// <see cref="DownloadHandle.BytesDownloaded"/> and recording it, so a race - if one exists -
    /// has room to actually manifest instead of the whole call finishing atomically by luck.</summary>
    [Fact]
    public void ReportProgress_ForcedConcurrently_NeverDeliversADecreasingTotal()
    {
        var handle = new DownloadHandle(new Uri("https://example.test/file"), "/tmp/does-not-matter", new DownloadOptions());
        handle.TotalBytes = 1_000_000;
        handle.InitializeChunks([(0, 999_999)]);

        var reportedTotals = new ConcurrentQueue<long>();
        handle.ProgressChanged += (_, progress) =>
        {
            Thread.Sleep(1);
            reportedTotals.Enqueue(progress.BytesDownloaded);
        };

        const int threadCount = 8;
        const int iterationsPerThread = 40;
        const int bytesPerIteration = 1000;

        var barrier = new Barrier(threadCount);
        Parallel.For(0, threadCount, _ =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < iterationsPerThread; i++)
            {
                handle.AddChunkBytesDownloaded(0, bytesPerIteration);
                handle.ReportProgress(force: true);
            }
        });

        var previous = -1L;
        foreach (var total in reportedTotals)
        {
            Assert.True(total >= previous, $"Progress went backward: {previous} -> {total}");
            previous = total;
        }

        Assert.Equal(threadCount * iterationsPerThread * bytesPerIteration, handle.BytesDownloaded);
    }
}
