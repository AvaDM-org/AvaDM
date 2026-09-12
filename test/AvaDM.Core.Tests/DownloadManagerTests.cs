using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AvaDM.Core;
using Xunit;

namespace AvaDM.Core.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("avadm-tests-").FullName;
    private string DatabasePath => Path.Combine(_tempDirectory, "avadm.db");

    [Fact]
    public async Task RemoveDownloadAsync_UnknownId_ReturnsNotFoundError()
    {
        using var client = CreateHttpClient();
        var manager = CreateManager(client);

        var result = await manager.RemoveDownloadAsync(Guid.NewGuid(), deleteFile: false);

        Assert.False(result.Success);
        Assert.Equal("No download found with that id.", result.Error);
    }

    [Fact]
    public async Task RemoveDownloadAsync_DeleteFileFalse_RemovesIndexAndKeepsDestination()
    {
        var destination = Path.Combine(_tempDirectory, "keep.bin");
        await File.WriteAllTextAsync(destination, "keep this file");
        var id = Guid.NewGuid();
        var repository = await SeedRecordAsync(id, "http://127.0.0.1/keep.bin", destination, DownloadState.Completed);

        using var client = CreateHttpClient();
        var manager = CreateManager(client);
        var result = await manager.RemoveDownloadAsync(id, deleteFile: false);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(await repository.GetByIdAsync(id));
        Assert.True(File.Exists(destination));
        Assert.Equal("keep this file", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task RemoveDownloadAsync_DeleteFileTrue_DeletesFinalDestinationAndSidecar()
    {
        var destination = Path.Combine(_tempDirectory, "delete.bin");
        var sidecar = destination + ".avadm";
        await File.WriteAllTextAsync(destination, "final");
        await File.WriteAllTextAsync(sidecar, "working");
        var id = Guid.NewGuid();
        var repository = await SeedRecordAsync(id, "http://127.0.0.1/delete.bin", destination, DownloadState.Completed);

        using var client = CreateHttpClient();
        var manager = CreateManager(client);
        var result = await manager.RemoveDownloadAsync(id, deleteFile: true);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(await repository.GetByIdAsync(id));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public async Task RemoveDownloadAsync_DeleteFileTrue_DeletesWorkingSidecarWhenFinalIsMissing()
    {
        var destination = Path.Combine(_tempDirectory, "in-progress.bin");
        var sidecar = destination + ".avadm";
        await File.WriteAllTextAsync(sidecar, "partial working data");
        var id = Guid.NewGuid();
        var repository = await SeedRecordAsync(id, "http://127.0.0.1/in-progress.bin", destination, DownloadState.Paused);

        using var client = CreateHttpClient();
        var manager = CreateManager(client);
        var result = await manager.RemoveDownloadAsync(id, deleteFile: true);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(await repository.GetByIdAsync(id));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(sidecar));
    }

    [Fact]
    public async Task RemoveDownloadAsync_ActiveHandle_CancelsDownloadAndRemovesIndex()
    {
        var payload = CreatePayload(512 * 1024);
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: true);
        var destination = Path.Combine(_tempDirectory, "cancelled.bin");
        using var client = CreateHttpClient();
        var manager = CreateManager(client);

        var added = await manager.AddDownloadAsync(server.Uri, destination);
        Assert.True(added.Success);
        Assert.NotNull(added.Id);
        Assert.NotNull(added.Handle);
        var id = added.Id!.Value;
        var handle = added.Handle!;

        var result = await manager.RemoveDownloadAsync(id, deleteFile: true);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.True(handle.Completion.IsCompleted);
        Assert.True(handle.Completion.IsCanceled || handle.Completion.IsFaulted);
        var completionException = await Record.ExceptionAsync(async () => await handle.Completion);
        Assert.NotNull(completionException);
        Assert.IsAssignableFrom<OperationCanceledException>(completionException);
        Assert.Null(await manager.GetDownloadAsync(id));
    }

    /// <summary>Guards against two handles ending up simultaneously active for the same download
    /// id - which could otherwise happen when, say, DownloadManager's internal auto-retry has
    /// already replaced a failed handle in the background (the UI never learns about that
    /// replacement directly) and a user, seeing a seemingly-stuck row, clicks Resume themselves.
    /// Without the stale-handle guard, both handles would hold their own open file handle on the
    /// same .avadm file and race each other to write/checkpoint/finalize it.</summary>
    [Fact]
    public async Task AddDownloadAsync_ResumeWhileHandleStillActive_CancelsStaleHandleBeforeStartingNew()
    {
        var payload = CreatePayload(512 * 1024);
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: true);
        var destination = Path.Combine(_tempDirectory, "still-active.bin");
        using var client = CreateHttpClient();
        var manager = CreateManager(client);

        var first = await manager.AddDownloadAsync(server.Uri, destination);
        Assert.True(first.Success);
        var id = first.Id!.Value;
        var staleHandle = first.Handle!;

        // Make sure the first download is genuinely in flight (its GET held open by the server)
        // before racing a second AddDownloadAsync against it.
        await server.GetHeadersSent;

        var second = await manager.AddDownloadAsync(server.Uri, destination, null, new ConflictResolution.Resume());

        Assert.True(second.Success);
        Assert.Equal(id, second.Id);
        Assert.NotSame(staleHandle, second.Handle);

        Assert.True(staleHandle.Completion.IsCompleted);
        var staleException = await Record.ExceptionAsync(async () => await staleHandle.Completion);
        Assert.IsAssignableFrom<OperationCanceledException>(staleException);

        // Must point at the new handle - not left null, and not clobbered back by the stale
        // handle's own finally-block cleanup running after the new one had already registered.
        Assert.Same(second.Handle, manager.GetActiveHandle(id));
    }

    [Fact]
    public async Task ResumeDownloadAsync_UnknownId_ReturnsNotFoundError()
    {
        using var client = CreateHttpClient();
        var manager = CreateManager(client);

        var result = await manager.ResumeDownloadAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal("No download found with that id.", result.Error);
        Assert.Null(result.Id);
        Assert.Null(result.Handle);
    }

    [Fact]
    public async Task ResumeDownloadAsync_CompletedRecord_DelegatesToAddAndReportsCompletedConflict()
    {
        var uri = new Uri("http://127.0.0.1/completed.bin");
        var destination = Path.Combine(_tempDirectory, "completed.bin");
        var id = Guid.NewGuid();
        var repository = await SeedRecordAsync(id, uri.AbsoluteUri, destination, DownloadState.Completed, 25);

        using var client = CreateHttpClient();
        var manager = CreateManager(client);
        var result = await manager.ResumeDownloadAsync(id);

        Assert.False(result.Success);
        Assert.Null(result.Id);
        Assert.Null(result.Handle);
        Assert.Equal("Download already completed.", result.Error);
        Assert.NotNull(result.Conflict);
        Assert.True(result.Conflict!.HasConflict);
        Assert.Equal(id, result.Conflict.ExistingRecord!.Id);
        Assert.Equal(uri.AbsoluteUri, result.Conflict.ExistingRecord.Uri);
        Assert.Equal(Path.GetFullPath(destination), result.Conflict.ExistingRecord.DestinationPath);
        Assert.NotNull(await repository.GetByIdAsync(id));
    }

    /// <summary>Regression test for #14: a HEAD response with no Content-Length used to throw
    /// (an unobserved-task-exception crash, #7) instead of falling back to a single,
    /// non-resumable stream. TotalBytes/the chunk's end byte should start unknown (0 / -1, see
    /// <see cref="DownloadHandle.FinalizeUnknownSizeDownload"/>) and be backfilled with the real
    /// size once the download finishes.</summary>
    [Fact]
    public async Task AddDownloadAsync_NoContentLength_FallsBackToSingleChunkAndBackfillsSizeOnCompletion()
    {
        var payload = CreatePayload(256 * 1024);
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: false, omitContentLength: true);
        var destination = Path.Combine(_tempDirectory, "unknown-size.bin");
        using var client = CreateHttpClient();
        var manager = CreateManager(client);

        var added = await manager.AddDownloadAsync(server.Uri, destination);
        Assert.True(added.Success);
        var handle = added.Handle!;

        // Wait until the GET response headers have gone out - by then HEAD has definitely
        // completed (chunks initialized) and the body write is only just starting, so the
        // still-unknown state below is guaranteed rather than racing HEAD's own completion.
        await server.GetHeadersSent;

        // TotalBytes and the chunk's end byte are still unknown while the HEAD response carried
        // no size - this is the "???" state the UI shows.
        Assert.Equal(0, handle.TotalBytes);
        var chunk = Assert.Single(handle.Chunks);
        Assert.True(chunk.EndByte < chunk.StartByte);

        await handle.Completion;

        Assert.Equal(DownloadState.Completed, handle.State);
        Assert.Equal(payload.Length, handle.TotalBytes);
        var finishedChunk = Assert.Single(handle.Chunks);
        Assert.Equal(0, finishedChunk.StartByte);
        Assert.Equal(payload.Length - 1, finishedChunk.EndByte);
        Assert.Equal(payload.Length, handle.BytesDownloaded);

        Assert.True(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".avadm"));

        // SyncToRepository's terminal-state DB write fires from a Completion continuation rather
        // than being awaited by AddDownloadAsync - give it a moment to land, same as
        // FailedDownload_AutoRetries_UpToConfiguredLimitThenStops below.
        await Task.Delay(200);
        var record = await manager.GetDownloadAsync(added.Id!.Value);
        Assert.NotNull(record);
        Assert.Equal(payload.Length, record!.TotalBytes);
    }

    /// <summary>Regression test for the inactivity-vs-attempt-duration timeout fix: a transfer
    /// that keeps making progress must never be retried just for taking longer overall than
    /// <see cref="DownloadSettings.DefaultInactivityTimeout"/> - only real silence should count.
    /// LocalHttpServer drips the payload in 8 KB chunks with a fixed 10ms gap between them
    /// (regardless of holdBody), so a big enough payload takes comfortably longer in total than
    /// the timeout configured here while no single gap does. Before the fix, this download (which
    /// - like every LocalHttpServer response - has no Accept-Ranges, so it runs through the
    /// non-resumable whole-file path) would have restarted from byte 0 on every timeout and never
    /// finished.</summary>
    [Fact]
    public async Task Download_SlowButSteadyTransfer_CompletesInOneAttemptDespitePassingInactivityTimeout()
    {
        var payload = CreatePayload(300 * 1024); // ~38 x 8KB writes x 10ms gap =~ 380ms total
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: false);
        var destination = Path.Combine(_tempDirectory, "slow-steady.bin");
        using var client = CreateHttpClient();
        var manager = new DownloadManager(client, new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultRetryBaseDelay = TimeSpan.Zero,
            // Shorter than the ~380ms total transfer time, but far longer than the ~10ms gap
            // between any two writes - a flat attempt-duration timeout of this length would have
            // fired partway through; a stall/inactivity timeout should not.
            DefaultInactivityTimeout = TimeSpan.FromMilliseconds(150),
        });

        var added = await manager.AddDownloadAsync(server.Uri, destination);
        Assert.True(added.Success);
        var handle = added.Handle!;

        var start = DateTime.UtcNow;
        await handle.Completion;
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(DownloadState.Completed, handle.State);
        Assert.True(elapsed > TimeSpan.FromMilliseconds(150),
            $"Expected the transfer to genuinely take longer than the inactivity timeout, took {elapsed}.");
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));

        // 1 HEAD + 1 GET - a retry (from an inactivity timeout misfiring) would show up as a
        // second GET connection.
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task FailedDownload_AutoRetries_UpToConfiguredLimitThenStops()
    {
        var handler = new AlwaysFailingHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = new DownloadManager(client, new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultAutoRetryAttempts = 3,
        });

        var destination = Path.Combine(_tempDirectory, "always-fails.bin");
        var added = await manager.AddDownloadAsync(new Uri("http://127.0.0.1/always-fails.bin"), destination);
        Assert.True(added.Success);
        var id = added.Id!.Value;

        // Each attempt fails on the HEAD request with no delay, so the initial attempt plus all
        // automatic retries settle almost immediately; poll rather than sleep a fixed amount.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.RequestCount < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        // Give a wrongly-unbounded retry loop a chance to prove itself before asserting it stopped.
        await Task.Delay(200);

        Assert.Equal(4, handler.RequestCount); // 1 initial attempt + 3 automatic retries.
        var record = await manager.GetDownloadAsync(id);
        Assert.Equal(DownloadState.Failed, record!.State);
    }

    [Fact]
    public async Task ResumeDownloadAsync_ManualCall_ResetsAutoRetryCounter()
    {
        var handler = new AlwaysFailingHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = new DownloadManager(client, new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultAutoRetryAttempts = 1,
        });

        var destination = Path.Combine(_tempDirectory, "always-fails-2.bin");
        var added = await manager.AddDownloadAsync(new Uri("http://127.0.0.1/always-fails-2.bin"), destination);
        Assert.True(added.Success);
        var id = added.Id!.Value;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.RequestCount < 2 && DateTime.UtcNow < deadline) // 1 initial + 1 auto-retry.
            await Task.Delay(25);
        await Task.Delay(200);
        Assert.Equal(2, handler.RequestCount);

        // Auto-retry budget (1) is now exhausted; a manual resume must still work and restart it.
        var resumed = await manager.ResumeDownloadAsync(id);
        Assert.True(resumed.Success);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.RequestCount < 4 && DateTime.UtcNow < deadline) // +1 manual + 1 more auto-retry.
            await Task.Delay(25);
        await Task.Delay(200);

        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task AddDownloadAsync_AtConcurrencyLimit_QueuesInsteadOfStarting()
    {
        var payload = CreatePayload(64 * 1024);
        await using var server1 = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var server2 = await LocalHttpServer.StartAsync(payload, holdBody: true);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 1);

        var first = await manager.AddDownloadAsync(server1.Uri, Path.Combine(_tempDirectory, "first.bin"));
        Assert.True(first.Success);
        Assert.NotNull(first.Handle);
        await server1.GetHeadersSent; // confirm it's genuinely occupying the one slot

        var second = await manager.AddDownloadAsync(server2.Uri, Path.Combine(_tempDirectory, "second.bin"));
        Assert.True(second.Success);
        Assert.Null(second.Handle);
        Assert.Null(manager.GetActiveHandle(second.Id!.Value));

        var record = await manager.GetDownloadAsync(second.Id!.Value);
        Assert.Equal(DownloadState.Queued, record!.State);
    }

    [Fact]
    public async Task QueuedDownload_StartsAutomatically_WhenRunningDownloadCompletes()
    {
        // Large enough that the drip (8 KB per 10ms, see LocalHttpServer) leaves a comfortable
        // window to add and assert the second download before the first finishes on its own.
        var slowPayload = CreatePayload(400 * 1024);
        var fastPayload = CreatePayload(4 * 1024);
        await using var server1 = await LocalHttpServer.StartAsync(slowPayload, holdBody: false);
        await using var server2 = await LocalHttpServer.StartAsync(fastPayload, holdBody: false);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 1);

        var first = await manager.AddDownloadAsync(server1.Uri, Path.Combine(_tempDirectory, "first.bin"));
        await server1.GetHeadersSent;

        var second = await manager.AddDownloadAsync(server2.Uri, Path.Combine(_tempDirectory, "second.bin"));
        Assert.Null(second.Handle);

        await first.Handle!.Completion;
        Assert.Equal(DownloadState.Completed, first.Handle.State);

        // Poll durable repository state, not the transient _activeHandles entry: second's payload
        // is tiny enough that it can start and finish (removing itself from _activeHandles again)
        // between two polls, so a handle-presence check can race straight past it.
        var record = await PollUntilStateAsync(manager, second.Id!.Value, DownloadState.Completed);
        Assert.Equal(DownloadState.Completed, record.State);
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "second.bin")));
    }

    [Fact]
    public async Task PausingRunningDownload_FreesSlot_ForQueuedDownloadToStart()
    {
        var heldPayload = CreatePayload(64 * 1024);
        var fastPayload = CreatePayload(4 * 1024);
        await using var server1 = await LocalHttpServer.StartAsync(heldPayload, holdBody: true);
        await using var server2 = await LocalHttpServer.StartAsync(fastPayload, holdBody: false);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 1);

        var first = await manager.AddDownloadAsync(server1.Uri, Path.Combine(_tempDirectory, "first.bin"));
        await server1.GetHeadersSent;

        var second = await manager.AddDownloadAsync(server2.Uri, Path.Combine(_tempDirectory, "second.bin"));
        Assert.Null(second.Handle);

        first.Handle!.Pause();

        var record = await PollUntilStateAsync(manager, second.Id!.Value, DownloadState.Completed);
        Assert.Equal(DownloadState.Completed, record.State);
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "second.bin")));

        // Clean up the still-paused, held-open first download rather than leaving it dangling.
        var cleanup = await manager.CancelDownloadAsync(first.Id!.Value);
        Assert.True(cleanup.Success);
    }

    /// <summary>Regression test for a bug found in manual UI testing of #25: pausing A frees its
    /// slot, B is added and takes it, then the user resumes A from the UI - which used to call
    /// DownloadHandle.Resume() directly on A's still-live (merely paused) handle, bypassing
    /// DownloadManager's admission check entirely and leaving both A and B running at once over a
    /// concurrency limit of 1. DownloadRowViewModel.Resume() no longer has that direct-resume fast
    /// path - every resume, paused or not, now goes through DownloadManager.ResumeDownloadAsync,
    /// which is admission-gated exactly like a fresh add.</summary>
    [Fact]
    public async Task ResumeDownloadAsync_PausedHandleWhoseSlotWasTakenByAnother_QueuesInsteadOfRunningBoth()
    {
        var payload = CreatePayload(64 * 1024);
        await using var serverA = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var serverB = await LocalHttpServer.StartAsync(payload, holdBody: true);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 1);

        var a = await manager.AddDownloadAsync(serverA.Uri, Path.Combine(_tempDirectory, "a.bin"));
        await serverA.GetHeadersSent;
        a.Handle!.Pause();

        var b = await manager.AddDownloadAsync(serverB.Uri, Path.Combine(_tempDirectory, "b.bin"));
        Assert.NotNull(b.Handle); // admitted immediately - A's pause freed the only slot
        await serverB.GetHeadersSent;

        var resumed = await manager.ResumeDownloadAsync(a.Id!.Value);

        Assert.True(resumed.Success);
        Assert.Null(resumed.Handle); // queued, not resumed - the slot is still taken by B
        Assert.Null(manager.GetActiveHandle(a.Id!.Value));
        Assert.NotNull(manager.GetActiveHandle(b.Id!.Value)); // B is unaffected, still the only one running

        var recordA = await manager.GetDownloadAsync(a.Id!.Value);
        Assert.Equal(DownloadState.Queued, recordA!.State);

        var cleanupA = await manager.CancelDownloadAsync(a.Id!.Value);
        Assert.True(cleanupA.Success);
        var cleanupB = await manager.CancelDownloadAsync(b.Id!.Value);
        Assert.True(cleanupB.Success);
    }

    [Fact]
    public async Task EnforceConcurrencyLimitAsync_LimitLoweredBelowRunningCount_PausesExcess()
    {
        var payload = CreatePayload(64 * 1024);
        await using var serverA = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var serverB = await LocalHttpServer.StartAsync(payload, holdBody: true);
        using var client = CreateHttpClient();
        var settings = new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultRetryBaseDelay = TimeSpan.Zero,
            DefaultInactivityTimeout = TimeSpan.FromSeconds(5),
            DefaultMaxConcurrentDownloads = 2,
        };
        var manager = new DownloadManager(client, settings);

        var a = await manager.AddDownloadAsync(serverA.Uri, Path.Combine(_tempDirectory, "a.bin"));
        var b = await manager.AddDownloadAsync(serverB.Uri, Path.Combine(_tempDirectory, "b.bin"));
        Assert.NotNull(a.Handle);
        Assert.NotNull(b.Handle);
        await serverA.GetHeadersSent;
        await serverB.GetHeadersSent;

        settings.DefaultMaxConcurrentDownloads = 1;
        await manager.EnforceConcurrencyLimitAsync();

        var aHandle = manager.GetActiveHandle(a.Id!.Value)!;
        var bHandle = manager.GetActiveHandle(b.Id!.Value)!;
        var states = new[] { aHandle.State, bHandle.State };
        Assert.Equal(1, states.Count(s => s == DownloadState.Running));
        Assert.Equal(1, states.Count(s => s == DownloadState.Paused));

        // B has the higher QueueOrder (added after A), so it's the one paused first.
        Assert.Equal(DownloadState.Running, aHandle.State);
        Assert.Equal(DownloadState.Paused, bHandle.State);

        var cleanupA = await manager.CancelDownloadAsync(a.Id!.Value);
        Assert.True(cleanupA.Success);
        var cleanupB = await manager.CancelDownloadAsync(b.Id!.Value);
        Assert.True(cleanupB.Success);
    }

    /// <summary>Regression test for a data-corruption bug found in manual testing of #25: two
    /// live handles ending up registered for the same download id, both writing the same .avadm
    /// file, eventually leaving the record marked Completed while its actual bytes on disk were
    /// incomplete/corrupted (and un-resumable, since a Completed record can't be resumed).
    /// AdmitQueuedDownloadsAsync used to trust the repository's Queued/not-Queued state blindly;
    /// it now skips any record that already has a live handle in _activeHandles regardless of
    /// what the (necessarily lagging) repository currently says - see TryStartHandleAsync's doc
    /// comment for the exact timing race this simulates directly instead of trying to induce.</summary>
    [Fact]
    public async Task AdmitQueuedDownloadsAsync_RecordAlreadyHasLiveHandle_DoesNotStartASecondOne()
    {
        var payload = CreatePayload(64 * 1024);
        await using var serverA = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var serverB = await LocalHttpServer.StartAsync(payload, holdBody: true);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 2);

        var a = await manager.AddDownloadAsync(serverA.Uri, Path.Combine(_tempDirectory, "a.bin"));
        var b = await manager.AddDownloadAsync(serverB.Uri, Path.Combine(_tempDirectory, "b.bin"));
        Assert.NotNull(a.Handle);
        Assert.NotNull(b.Handle);
        await serverA.GetHeadersSent;
        await serverB.GetHeadersSent;

        b.Handle!.Pause(); // frees B's slot in RunningHandleCount, but its live handle still exists

        // Simulate the repository still (or again) saying Queued for a download that already has
        // a live handle - exactly the state TryStartHandleAsync's widened lock now prevents from
        // being observed mid-registration, reproduced directly here instead of via timing.
        var repository = new DownloadRepository(DatabasePath);
        await repository.UpdateStateAsync(b.Id!.Value, DownloadState.Queued);

        await manager.AdmitQueuedDownloadsAsync();

        Assert.Same(b.Handle, manager.GetActiveHandle(b.Id!.Value)); // no duplicate handle started
        Assert.Equal(DownloadState.Paused, manager.GetActiveHandle(b.Id!.Value)!.State); // untouched

        var cleanupA = await manager.CancelDownloadAsync(a.Id!.Value);
        Assert.True(cleanupA.Success);
        var cleanupB = await manager.CancelDownloadAsync(b.Id!.Value);
        Assert.True(cleanupB.Success);
    }

    [Fact]
    public async Task MoveQueuedDownloadUpAsync_ChangesAdmissionOrder()
    {
        var payload = CreatePayload(64 * 1024);
        // B and C hold their bodies open once admitted, so whichever one wins the single slot
        // stays observably Running rather than racing straight through to completion - the same
        // reason the queuedCount/QueuedDownload_StartsAutomatically tests poll repository state
        // instead of a live handle.
        await using var occupying = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var serverB = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var serverC = await LocalHttpServer.StartAsync(payload, holdBody: true);
        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 1);

        var occupant = await manager.AddDownloadAsync(occupying.Uri, Path.Combine(_tempDirectory, "occupant.bin"));
        await occupying.GetHeadersSent;

        var queuedB = await manager.AddDownloadAsync(serverB.Uri, Path.Combine(_tempDirectory, "b.bin"));
        var queuedC = await manager.AddDownloadAsync(serverC.Uri, Path.Combine(_tempDirectory, "c.bin"));
        Assert.Null(queuedB.Handle);
        Assert.Null(queuedC.Handle);

        // C was added after B, so it starts behind B by default - move it to the front.
        var moved = await manager.MoveQueuedDownloadUpAsync(queuedC.Id!.Value);
        Assert.True(moved);

        var cancelled = await manager.CancelDownloadAsync(occupant.Id!.Value);
        Assert.True(cancelled.Success);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (manager.GetActiveHandle(queuedC.Id!.Value) is null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotNull(manager.GetActiveHandle(queuedC.Id!.Value));
        Assert.Null(manager.GetActiveHandle(queuedB.Id!.Value));

        var cleanupC = await manager.CancelDownloadAsync(queuedC.Id!.Value);
        Assert.True(cleanupC.Success);
    }

    [Fact]
    public async Task ResumeAllInterruptedAsync_AdmitsOnlyUpToTheConcurrencyLimit()
    {
        var payload = CreatePayload(64 * 1024);
        await using var server1 = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var server2 = await LocalHttpServer.StartAsync(payload, holdBody: true);
        await using var server3 = await LocalHttpServer.StartAsync(payload, holdBody: true);

        // Seed three rows as if left "Running" by a previous process - no live handle exists for
        // any of them yet, matching what the UI shows as "Interrupted".
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();
        var repository = new DownloadRepository(DatabasePath);
        await repository.InitializeAsync();
        await repository.InsertAsync(id1, server1.Uri.AbsoluteUri, Path.Combine(_tempDirectory, "r1.bin"), DownloadState.Running, 1);
        await repository.InsertAsync(id2, server2.Uri.AbsoluteUri, Path.Combine(_tempDirectory, "r2.bin"), DownloadState.Running, 1);
        await repository.InsertAsync(id3, server3.Uri.AbsoluteUri, Path.Combine(_tempDirectory, "r3.bin"), DownloadState.Running, 1);

        using var client = CreateHttpClient();
        var manager = CreateManager(client, maxConcurrentDownloads: 2);

        await manager.ResumeAllInterruptedAsync();

        var handles = new[] { id1, id2, id3 }.Select(manager.GetActiveHandle).ToList();
        Assert.Equal(2, handles.Count(h => h is not null));
        Assert.Equal(1, handles.Count(h => h is null));
    }

    [Fact]
    public async Task AutoResumeDownloadsOnStartup_True_ResumesInterruptedRowOnFirstUse()
    {
        var payload = CreatePayload(4 * 1024);
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: false);
        var id = Guid.NewGuid();
        var repository = new DownloadRepository(DatabasePath);
        await repository.InitializeAsync();
        await repository.InsertAsync(id, server.Uri.AbsoluteUri, Path.Combine(_tempDirectory, "startup.bin"), DownloadState.Running, 1);

        using var client = CreateHttpClient();
        var manager = CreateManager(client, autoResumeDownloadsOnStartup: true);

        // Any call is enough to trigger EnsureInitializedAsync's one-time startup rehydration.
        await manager.GetAllDownloadsAsync();

        var record = await PollUntilStateAsync(manager, id, DownloadState.Completed);
        Assert.Equal(DownloadState.Completed, record.State);
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "startup.bin")));
    }

    [Fact]
    public async Task AutoResumeDownloadsOnStartup_False_LeavesInterruptedRowUntouched()
    {
        var payload = CreatePayload(4 * 1024);
        await using var server = await LocalHttpServer.StartAsync(payload, holdBody: false);
        var id = Guid.NewGuid();
        var repository = new DownloadRepository(DatabasePath);
        await repository.InitializeAsync();
        await repository.InsertAsync(id, server.Uri.AbsoluteUri, Path.Combine(_tempDirectory, "no-startup.bin"), DownloadState.Running, 1);

        using var client = CreateHttpClient();
        var manager = CreateManager(client); // AutoResumeDownloadsOnStartup defaults to false

        await manager.GetAllDownloadsAsync();
        await Task.Delay(200); // give a wrongly-auto-resuming implementation a chance to prove itself

        Assert.Null(manager.GetActiveHandle(id));
        var record = await manager.GetDownloadAsync(id);
        Assert.Equal(DownloadState.Running, record!.State); // untouched, not flipped to Queued either
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private DownloadManager CreateManager(HttpClient client, int? maxConcurrentDownloads = null, bool autoResumeDownloadsOnStartup = false) =>
        new(client, new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultRetryBaseDelay = TimeSpan.Zero,
            DefaultInactivityTimeout = TimeSpan.FromSeconds(5),
            DefaultMaxConcurrentDownloads = maxConcurrentDownloads ?? new DownloadSettings().DefaultMaxConcurrentDownloads,
            AutoResumeDownloadsOnStartup = autoResumeDownloadsOnStartup,
        });

    private HttpClient CreateHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Polls the repository (not the transient <see cref="DownloadManager.GetActiveHandle"/>
    /// map) until a download reaches <paramref name="expected"/> or a 5-second deadline passes -
    /// a small/fast download can start and finish between two polls, taking itself back out of
    /// the live-handle map before a check ever catches it there.</summary>
    private static async Task<DownloadRecord> PollUntilStateAsync(DownloadManager manager, Guid id, DownloadState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        DownloadRecord? record;
        while ((record = await manager.GetDownloadAsync(id))!.State != expected && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        return record!;
    }

    private async Task<DownloadRepository> SeedRecordAsync(
        Guid id,
        string uri,
        string destination,
        DownloadState state,
        long totalBytes = 1)
    {
        var repository = new DownloadRepository(DatabasePath);
        await repository.InitializeAsync();
        await repository.InsertAsync(id, uri, destination, state, totalBytes);
        return repository;
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        return payload;
    }

    /// <summary>Fails every request immediately with no network I/O, so a download attempt faults
    /// as fast as possible - lets auto-retry tests assert exact attempt counts without relying on
    /// timing-sensitive network behavior.</summary>
    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new HttpRequestException("Simulated connection failure.");
        }
    }

    private sealed class LocalHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly bool _holdBody;
        private readonly bool _omitContentLength;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly TaskCompletionSource<bool> _getHeadersSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _bodyGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private LocalHttpServer(byte[] payload, bool holdBody, bool omitContentLength)
        {
            _payload = payload;
            _holdBody = holdBody;
            _omitContentLength = omitContentLength;
            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Uri = new Uri($"http://127.0.0.1:{endpoint.Port}/payload.bin");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri Uri { get; }
        public Task GetHeadersSent => _getHeadersSent.Task;

        /// <summary>Total TCP connections accepted so far (one per HEAD or GET - every response
        /// sends <c>Connection: close</c>). Used to detect a retried/restarted request: a HEAD
        /// plus exactly one GET is 2, a retried GET would push it to 3+.</summary>
        public int ConnectionCount => _connections.Count;

        public static Task<LocalHttpServer> StartAsync(byte[] payload, bool holdBody, bool omitContentLength = false) =>
            Task.FromResult(new LocalHttpServer(payload, holdBody, omitContentLength));

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var connection = HandleClientAsync(client);
                    _connections.Add(connection);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, _stop.Token);
                    if (request is null)
                        return;

                    var contentLength = _omitContentLength ? (int?)null : _payload.Length;
                    var method = request.Split(' ', 2)[0];
                    if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseHeadersAsync(stream, 200, contentLength, _stop.Token);
                        return;
                    }

                    if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseHeadersAsync(stream, 405, 0, _stop.Token);
                        return;
                    }

                    await WriteResponseHeadersAsync(stream, 200, contentLength, _stop.Token);
                    _getHeadersSent.TrySetResult(true);
                    if (_holdBody)
                        await _bodyGate.Task.WaitAsync(_stop.Token);

                    const int chunkSize = 8192;
                    for (var offset = 0; offset < _payload.Length; offset += chunkSize)
                    {
                        var count = Math.Min(chunkSize, _payload.Length - offset);
                        await stream.WriteAsync(_payload.AsMemory(offset, count), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        await Task.Delay(TimeSpan.FromMilliseconds(10), _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                    // The client closing a cancelled request is expected.
                }
                catch (SocketException)
                {
                    // The client closing a cancelled request is expected.
                }
            }
        }

        private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (bytes.Count < 64 * 1024)
            {
                var count = await stream.ReadAsync(buffer, ct);
                if (count == 0)
                    return null;
                bytes.Add(buffer[0]);
                var length = bytes.Count;
                if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
                    bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                {
                    return Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", 2)[0];
                }
            }

            return null;
        }

        private static async Task WriteResponseHeadersAsync(
            NetworkStream stream, int statusCode, int? contentLength, CancellationToken ct)
        {
            var reason = statusCode == 200 ? "OK" : "Method Not Allowed";
            var contentLengthLine = contentLength.HasValue ? $"Content-Length: {contentLength}\r\n" : "";
            var response = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                           contentLengthLine +
                           "Content-Type: application/octet-stream\r\n" +
                           "Connection: close\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            _bodyGate.TrySetCanceled(_stop.Token);
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

            await Task.WhenAll(_connections.ToArray());
            _stop.Dispose();
        }
    }
}
