using System.Collections.Concurrent;
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

    private DownloadManager CreateManager(HttpClient client) =>
        new(client, new DownloadSettings
        {
            RepositoryPath = DatabasePath,
            DefaultDownloadDirectory = _tempDirectory,
            DefaultMaxRetryAttempts = 1,
            DefaultRetryBaseDelay = TimeSpan.Zero,
            DefaultPerAttemptTimeout = TimeSpan.FromSeconds(5)
        });

    private HttpClient CreateHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

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
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private readonly ConcurrentBag<Task> _connections = new();
        private readonly TaskCompletionSource<bool> _getHeadersSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _bodyGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private LocalHttpServer(byte[] payload, bool holdBody)
        {
            _payload = payload;
            _holdBody = holdBody;
            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            Uri = new Uri($"http://127.0.0.1:{endpoint.Port}/payload.bin");
            _acceptLoop = AcceptLoopAsync();
        }

        public Uri Uri { get; }
        public Task GetHeadersSent => _getHeadersSent.Task;

        public static Task<LocalHttpServer> StartAsync(byte[] payload, bool holdBody) =>
            Task.FromResult(new LocalHttpServer(payload, holdBody));

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

                    var method = request.Split(' ', 2)[0];
                    if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseHeadersAsync(stream, 200, _payload.Length, _stop.Token);
                        return;
                    }

                    if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseHeadersAsync(stream, 405, 0, _stop.Token);
                        return;
                    }

                    await WriteResponseHeadersAsync(stream, 200, _payload.Length, _stop.Token);
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
            NetworkStream stream, int statusCode, int contentLength, CancellationToken ct)
        {
            var reason = statusCode == 200 ? "OK" : "Method Not Allowed";
            var response = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                           $"Content-Length: {contentLength}\r\n" +
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
