// Interactive test harness for the AvaDM.Core download engine. Lets you drive one or more
// downloads at once from the command line - start/pause/resume/speed/cancel - so pause and
// speed-limit behavior (including multiple concurrent downloads, each its own file with its own
// parallel chunks) can be exercised live before any UI exists. Rendered via Terminal.Gui: a
// download list, a scrolling log, and a command line, all managed by the TUI's own event loop.

using AvaDM.Console;
using AvaDM.Core;
using AvaDM.Core.Diagnostics;

AppLogging.Initialize();
AppLogging.InstallGlobalExceptionHandlers(CrashReporter.Report);

var dashboard = new DownloadDashboard();

using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var settings = new DownloadSettings();
var manager = new DownloadManager(httpClient, settings);

var handles = new Dictionary<string, (Guid Id, DownloadHandle Handle)>();
var nextId = 1;

dashboard.CommandEntered += line =>
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
        return;

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

            case "setpath":
                SetDefaultPath(parts);
                break;

            case "status":
                Status(parts);
                break;

            case "cancel":
                WithHandle(parts, h => h.Cancel());
                break;

            case "quit":
            case "exit":
                dashboard.RequestQuit();
                break;

            default:
                dashboard.Log($"Unknown command '{command}'. Type 'help' for commands.");
                break;
        }
    }
    catch (Exception ex)
    {
        dashboard.Log($"Error: {ex.Message}");
    }
};

dashboard.Log("AvaDM Console - type 'help' for commands.");
dashboard.Run();
return;

void PrintHelp()
{
    dashboard.Log($"""
        Commands:
          start <url> [destPath] [chunkCount]   Start a download, prints its id (d1, d2, ...)
                                                 destPath may be omitted (uses the default
                                                 download directory), a directory - existing
                                                 or not, e.g. "./tests" - (the filename is
                                                 taken from the url), or a full file path
                                                 with an extension (e.g. "./tests/out.exe")
          pause <id>                            Pause a running download
          resume <id>                           Resume a paused download
          speed <id> <bytesPerSec|off>          Set/clear the download's speed limit
          setpath <dir>                         Set the default download directory
                                                 (currently {settings.DefaultDownloadDirectory})
          status [id]                           Show progress for one or all downloads
          cancel <id>                            Cancel a download
          quit | exit                           Exit
        """);
}

async void Start(string[] parts)
{
    if (parts.Length < 2)
    {
        dashboard.Log("Usage: start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]");
        return;
    }

    // Parse flags: scan all tokens and extract --resume/--overwrite/--rename, removing them before positional parsing
    ConflictResolution? resolution = null;
    var positionalParts = new List<string> { parts[0] }; // Keep "start"

    for (var i = 1; i < parts.Length; i++)
    {
        if (parts[i].Equals("--resume", StringComparison.OrdinalIgnoreCase))
        {
            resolution = new ConflictResolution.Resume();
        }
        else if (parts[i].Equals("--overwrite", StringComparison.OrdinalIgnoreCase))
        {
            resolution = new ConflictResolution.Overwrite();
        }
        else if (parts[i].Equals("--rename", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= parts.Length)
            {
                dashboard.Log("Error: --rename requires a destination path argument.");
                return;
            }
            resolution = new ConflictResolution.RenameDestination(parts[i + 1]);
            i++; // Skip the next token since we consumed it as the rename path
        }
        else
        {
            positionalParts.Add(parts[i]);
        }
    }

    var posArray = positionalParts.ToArray();
    if (posArray.Length < 2)
    {
        dashboard.Log("Usage: start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]");
        return;
    }

    var uri = new Uri(posArray[1]);
    var destPath = posArray.Length > 2 ? posArray[2] : null;
    int? chunkCount = posArray.Length > 3 && int.TryParse(posArray[3], out var parsed) ? parsed : null;

    var result = await manager.AddDownloadAsync(uri, destPath, new DownloadOptions { ChunkCount = chunkCount }, resolution);

    if (!result.Success)
    {
        if (result.Conflict?.HasConflict == true)
        {
            var existing = result.Conflict.ExistingRecord;
            dashboard.Log(
                $"Conflict: '{uri}' is already downloading to '{existing!.DestinationPath}' " +
                $"(state: {existing.State}, {existing.BytesDownloaded:N0}/{existing.TotalBytes:N0} bytes). " +
                $"Retry with --resume, --overwrite, or --rename <newPath>.");
        }
        else
        {
            dashboard.Log($"Error: {result.Error}");
        }
        return;
    }

    var handle = result.Handle!;
    var id = $"d{nextId++}";
    handles[id] = (result.Id!.Value, handle);
    dashboard.Track(id);

    handle.ProgressChanged += (_, progress) => dashboard.UpdateProgress(id, progress);
    handle.ChunksChanged += (_, chunks) => dashboard.UpdateChunks(id, chunks);
    handle.LogMessage += (_, message) => dashboard.Log($"[{id}] {message}");
    // Fire-and-forget with error logging: the dashboard keeps accepting commands for other
    // downloads (or new ones) while this one runs in the background.
    _ = handle.Completion.ContinueWith(
        t => dashboard.Log($"[{id}] failed: {t.Exception?.GetBaseException().Message}"),
        TaskContinuationOptions.OnlyOnFaulted);

    dashboard.Log($"[{id}] started -> {handle.DestinationPath}");
}

void SetDefaultPath(string[] parts)
{
    if (parts.Length < 2)
    {
        dashboard.Log($"Usage: setpath <dir>  (currently {settings.DefaultDownloadDirectory})");
        return;
    }

    settings.DefaultDownloadDirectory = parts[1];
    dashboard.Log($"Default download directory set to {parts[1]}.");
}

void WithHandle(string[] parts, Action<DownloadHandle> action)
{
    if (parts.Length < 2)
    {
        dashboard.Log($"Usage: {parts[0]} <id>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var entry))
    {
        dashboard.Log($"No download with id '{parts[1]}'.");
        return;
    }

    action(entry.Handle);
}

void SetSpeed(string[] parts)
{
    if (parts.Length < 3)
    {
        dashboard.Log("Usage: speed <id> <bytesPerSec|off>");
        return;
    }

    if (!handles.TryGetValue(parts[1], out var entry))
    {
        dashboard.Log($"No download with id '{parts[1]}'.");
        return;
    }

    var handle = entry.Handle;
    if (string.Equals(parts[2], "off", StringComparison.OrdinalIgnoreCase))
    {
        handle.SetSpeedLimit(null);
        dashboard.Log($"[{parts[1]}] speed limit removed.");
        return;
    }

    if (!long.TryParse(parts[2], out var bytesPerSecond) || bytesPerSecond <= 0)
    {
        dashboard.Log("Speed must be a positive number of bytes/sec, or 'off'.");
        return;
    }

    handle.SetSpeedLimit(bytesPerSecond);
    dashboard.Log($"[{parts[1]}] speed limit set to {bytesPerSecond:N0} B/s.");
}

void Status(string[] parts)
{
    if (handles.Count == 0)
    {
        dashboard.Log("No downloads yet.");
        return;
    }

    var ids = parts.Length > 1 ? new[] { parts[1] } : handles.Keys.ToArray();
    foreach (var id in ids)
    {
        if (!handles.TryGetValue(id, out var entry))
        {
            dashboard.Log($"No download with id '{id}'.");
            continue;
        }

        var handle = entry.Handle;
        dashboard.Log($"[{id}] {handle.State} {handle.BytesDownloaded:N0}/{handle.TotalBytes:N0} bytes ({handle.DestinationPath})");
    }
}