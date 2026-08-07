// Interactive test harness for the AvaDM.Core download engine. Lets you drive one or more
// downloads at once from the command line - start/pause/resume/speed/cancel - so pause and
// speed-limit behavior (including multiple concurrent downloads) can be exercised live before
// any UI exists.

using AvaDM.Core;

Console.WriteLine("AvaDM Console - type 'help' for commands.");

using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var pipeline = ChunkResiliencePipelineFactory.Create();
var downloader = new Downloader(httpClient, pipeline);

var handles = new Dictionary<string, DownloadHandle>();
var nextId = 1;

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null)
        break;

    // Named to avoid shadowing the top-level statements' implicit `args` (Main's argv).
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
        continue;

    var command = parts[0].ToLowerInvariant();
    try
    {
        switch (command)
        {
            case "help":
                PrintHelp();
                break;

            case "start":
                Start(parts);
                break;

            case "pause":
                WithHandle(parts, h => h.Pause());
                break;

            case "resume":
                WithHandle(parts, h => h.Resume());
                break;

            case "speed":
                SetSpeed(parts);
                break;

            case "status":
                Status(parts);
                break;

            case "cancel":
                WithHandle(parts, h => h.Cancel());
                break;

            case "quit":
            case "exit":
                return;

            default:
                Console.WriteLine($"Unknown command '{command}'. Type 'help' for commands.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

void PrintHelp()
{
    Console.WriteLine("""
        Commands:
          start <url> [destPath] [chunkCount]   Start a download, prints its id (d1, d2, ...)
          pause <id>                            Pause a running download
          resume <id>                           Resume a paused download
          speed <id> <bytesPerSec|off>          Set/clear the download's speed limit
          status [id]                           Show progress for one or all downloads
          cancel <id>                           Cancel a download
          quit | exit                           Exit
        """);
}

void Start(string[] parts)
{
    if (parts.Length < 2)
    {
        Console.WriteLine("Usage: start <url> [destPath] [chunkCount]");
        return;
    }

    var uri = new Uri(parts[1]);
    var destPath = parts.Length > 2 ? parts[2] : Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(uri.LocalPath));
    var chunkCount = parts.Length > 3 && int.TryParse(parts[3], out var parsed) ? parsed : 5;

    var id = $"d{nextId++}";
    var handle = downloader.StartDownload(uri, destPath, new DownloadOptions { ChunkCount = chunkCount });
    handles[id] = handle;

    handle.ProgressChanged += (_, progress) => PrintProgress(id, progress);
    // Fire-and-forget with error logging: the REPL keeps accepting commands for other
    // downloads (or new ones) while this one runs in the background.
    _ = handle.Completion.ContinueWith(
        t => Console.WriteLine($"[{id}] failed: {t.Exception?.GetBaseException().Message}"),
        TaskContinuationOptions.OnlyOnFaulted);

    Console.WriteLine($"[{id}] started -> {destPath}");
}

void WithHandle(string[] parts, Action<DownloadHandle> action)
{
    if (parts.Length < 2)
    {
        Console.WriteLine($"Usage: {parts[0]} <id>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var handle))
    {
        Console.WriteLine($"No download with id '{parts[1]}'.");
        return;
    }

    action(handle);
}

void SetSpeed(string[] parts)
{
    if (parts.Length < 3)
    {
        Console.WriteLine("Usage: speed <id> <bytesPerSec|off>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var handle))
    {
        Console.WriteLine($"No download with id '{parts[1]}'.");
        return;
    }

    if (string.Equals(parts[2], "off", StringComparison.OrdinalIgnoreCase))
    {
        handle.SetSpeedLimit(null);
        Console.WriteLine($"[{parts[1]}] speed limit removed.");
        return;
    }

    if (!long.TryParse(parts[2], out var bytesPerSecond) || bytesPerSecond <= 0)
    {
        Console.WriteLine("Speed must be a positive number of bytes/sec, or 'off'.");
        return;
    }

    handle.SetSpeedLimit(bytesPerSecond);
    Console.WriteLine($"[{parts[1]}] speed limit set to {bytesPerSecond:N0} B/s.");
}

void Status(string[] parts)
{
    if (handles.Count == 0)
    {
        Console.WriteLine("No downloads yet.");
        return;
    }

    var ids = parts.Length > 1 ? new[] { parts[1] } : handles.Keys.ToArray();
    foreach (var id in ids)
    {
        if (!handles.TryGetValue(id, out var handle))
        {
            Console.WriteLine($"No download with id '{id}'.");
            continue;
        }

        Console.WriteLine($"[{id}] {handle.State} {handle.BytesDownloaded:N0}/{handle.TotalBytes:N0} bytes ({handle.DestinationPath})");
    }
}

void PrintProgress(string id, DownloadProgress progress)
{
    var speed = progress.SpeedBytesPerSecond is { } s ? $"{s:N0} B/s" : "-";
    Console.WriteLine($"[{id}] {progress.State} {progress.BytesDownloaded:N0}/{progress.TotalBytes:N0} bytes @ {speed}");
}
