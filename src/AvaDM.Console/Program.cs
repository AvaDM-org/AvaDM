// Interactive test harness for the AvaDM.Core download engine. Lets you drive one or more
// downloads at once from the command line - start/pause/resume/speed/cancel - so pause and
// speed-limit behavior (including multiple concurrent downloads) can be exercised live before
// any UI exists. Progress is rendered in place (one row per download, redrawn where it stands)
// rather than as a continuous stream of lines, so the prompt stays put and easy to type into.

using AvaDM.Console;
using AvaDM.Core;

var panel = new ConsoleStatusPanel();
panel.Log("AvaDM Console - type 'help' for commands.");

using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var pipeline = ChunkResiliencePipelineFactory.Create(
    onRetry: (attempt, delay, ex) =>
        panel.Log($"Chunk request failed (attempt {attempt}), retrying in {delay.TotalSeconds:0.0}s: {ex?.Message}"));
var downloader = new Downloader(httpClient, pipeline);

var handles = new Dictionary<string, DownloadHandle>();
var nextId = 1;

while (true)
{
    var line = panel.ReadCommand();
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
                panel.Log($"Unknown command '{command}'. Type 'help' for commands.");
                break;
        }
    }
    catch (Exception ex)
    {
        panel.Log($"Error: {ex.Message}");
    }
}

void PrintHelp()
{
    panel.Log("""
        Commands:
          start <url> [destPath] [chunkCount]   Start a download, prints its id (d1, d2, ...)
          pause <id>                            Pause a running download
          resume <id>                           Resume a paused download
          speed <id> <bytesPerSec|off>          Set/clear the download's speed limit
          status [id]                           Show progress for one or all downloads
          cancel <id>                            Cancel a download
          quit | exit                           Exit
        """);
}

void Start(string[] parts)
{
    if (parts.Length < 2)
    {
        panel.Log("Usage: start <url> [destPath] [chunkCount]");
        return;
    }

    var uri = new Uri(parts[1]);
    var destPath = parts.Length > 2 ? parts[2] : Path.Combine(Directory.GetCurrentDirectory(), Path.GetFileName(uri.LocalPath));
    var chunkCount = parts.Length > 3 && int.TryParse(parts[3], out var parsed) ? parsed : 5;

    var id = $"d{nextId++}";
    var handle = downloader.StartDownload(uri, destPath, new DownloadOptions { ChunkCount = chunkCount });
    handles[id] = handle;
    panel.Track(id);

    handle.ProgressChanged += (_, progress) => panel.UpdateProgress(id, progress);
    handle.LogMessage += (_, message) => panel.Log($"[{id}] {message}");
    // Fire-and-forget with error logging: the REPL keeps accepting commands for other
    // downloads (or new ones) while this one runs in the background.
    _ = handle.Completion.ContinueWith(
        t => panel.Log($"[{id}] failed: {t.Exception?.GetBaseException().Message}"),
        TaskContinuationOptions.OnlyOnFaulted);

    panel.Log($"[{id}] started -> {destPath}");
}

void WithHandle(string[] parts, Action<DownloadHandle> action)
{
    if (parts.Length < 2)
    {
        panel.Log($"Usage: {parts[0]} <id>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var handle))
    {
        panel.Log($"No download with id '{parts[1]}'.");
        return;
    }

    action(handle);
}

void SetSpeed(string[] parts)
{
    if (parts.Length < 3)
    {
        panel.Log("Usage: speed <id> <bytesPerSec|off>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var handle))
    {
        panel.Log($"No download with id '{parts[1]}'.");
        return;
    }

    if (string.Equals(parts[2], "off", StringComparison.OrdinalIgnoreCase))
    {
        handle.SetSpeedLimit(null);
        panel.Log($"[{parts[1]}] speed limit removed.");
        return;
    }

    if (!long.TryParse(parts[2], out var bytesPerSecond) || bytesPerSecond <= 0)
    {
        panel.Log("Speed must be a positive number of bytes/sec, or 'off'.");
        return;
    }

    handle.SetSpeedLimit(bytesPerSecond);
    panel.Log($"[{parts[1]}] speed limit set to {bytesPerSecond:N0} B/s.");
}

void Status(string[] parts)
{
    if (handles.Count == 0)
    {
        panel.Log("No downloads yet.");
        return;
    }

    var ids = parts.Length > 1 ? new[] { parts[1] } : handles.Keys.ToArray();
    foreach (var id in ids)
    {
        if (!handles.TryGetValue(id, out var handle))
        {
            panel.Log($"No download with id '{id}'.");
            continue;
        }

        panel.Log($"[{id}] {handle.State} {handle.BytesDownloaded:N0}/{handle.TotalBytes:N0} bytes ({handle.DestinationPath})");
    }
}
