# AvaDM

Cross-platform (Linux/Windows, macOS later) open-source download manager in C#/.NET — a modern alternative to XDM/IDM/FDM. Name = **Ava**lonia + **DM**.

## Structure

- `src/AvaDM.Core` — download engine and persistence. `Downloader.cs` is the transfer engine; `DownloadManager.cs` is the UI-agnostic orchestration layer.
- `src/AvaDM.Console` — Terminal.Gui console harness for exercising multiple downloads and their controls during development; predates the Avalonia UI and is kept as a lightweight way to drive the engine directly.
- `src/AvaDM.UI` — Avalonia desktop UI (the primary, user-facing app). Runs on Linux/Windows now; macOS is untested but not expected to need engine-level changes.
- `test/AvaDM.Core.Tests` — xUnit tests for the core engine (`DownloadManagerTests`, `SpeedTrackerTests`).

## Current engine

The core targets `net10.0` and exposes a live `DownloadHandle` for each transfer. A `DownloadManager` owns a small SQLite index and delegates transfer work to `Downloader`.

### HTTP transfer mechanism

1. Send `HEAD` and require `Content-Length`; inspect `Accept-Ranges: bytes`.
2. For range-capable servers, split the file into concurrent ranges (five by default, configurable through `DownloadSettings` or `DownloadOptions`).
3. Pre-allocate `<destination>.avadm`, open one shared handle with `File.OpenHandle`, and write each response with `RandomAccess.WriteAsync` at explicit offsets. This avoids a shared `FileStream` cursor and permits non-overlapping chunk tasks to write safely.
4. Send each range request with `HttpCompletionOption.ResponseHeadersRead` and stream response bytes directly to disk.
5. If the server does not support ranges, fall back to one sequential whole-file request. This path is not resumable.
6. On success, truncate the footer and move the working file to the final destination. Failed, cancelled, or paused work retains the `.avadm` working file.

### Pause, resume, retry, and throttling

- `DownloadHandle.Pause()`/`Resume()` use a cooperative async pause gate shared by all chunk tasks.
- The `.avadm` file contains a binary footer with the source URI, total size, chunk ranges, statuses, and byte counts. The footer is checkpointed about every five seconds and once more when a run stops.
- A later process can resume by adding the same URL and destination with `--resume` (or the equivalent `ConflictResolution.Resume`). The footer is checked against the fresh URL/size and file length; missing, corrupt, stale, or mismatched data causes a safe fresh start rather than an exception. A completed indexed download is not resumed.
- Polly retries transient connection errors, I/O errors, timeouts, HTTP 408/429/5xx responses, with exponential backoff and jitter. Defaults to five retries and a 30-second per-attempt timeout; configurable via `DownloadSettings.DefaultMaxRetryAttempts`, `DefaultRetryBaseDelay`, and `DefaultPerAttemptTimeout`. The resilience pipeline is built fresh per download from current settings, so changes take effect on the next download started.
- A per-download token-bucket speed limiter is shared by all of that download's chunks, so a limit applies to aggregate throughput rather than once per chunk. It can be changed while running.

### Persistence and conflict handling

- SQLite stores one record per `(Uri, DestinationPath)` in the platform data directory by default (`%LocalAppData%/AvaDM/avadm.db` on Windows and the corresponding .NET local-application-data directory elsewhere). The path is configurable with `DownloadSettings.RepositoryPath`.
- `DownloadManager` updates aggregate progress periodically and always records the terminal state. It supports multiple independent active handles, but does not yet automatically rehydrate/start persisted downloads on application startup.
- Adding an existing `(URL, destination)` without a resolution is rejected with a conflict. Callers must explicitly choose resume, overwrite, or a different destination (`--resume`, `--overwrite`, or `--rename <path>`).

### Public UI-facing surface

`DownloadHandle` provides:

- aggregate state (`Pending`, `Running`, `Paused`, `Completed`, `Failed`, `Cancelled`), bytes, total size, average speed, destination, and completion task;
- per-chunk snapshots (`ChunkProgress`) and `ProgressChanged`, `ChunksChanged`, and informational `LogMessage` events;
- `Pause`, `Resume`, `Cancel`, and `SetSpeedLimit` controls.

`DownloadManager` provides conflict checking/resolution, starting downloads, persisted records, and lookup of a live handle for downloads in the current process.

## Desktop UI (AvaDM.UI)

Avalonia 12 + CommunityToolkit.Mvvm. No DI container — `App.axaml.cs` hand-wires the object graph (settings, `HttpClient`, `DownloadManager`, view models) once in `OnFrameworkInitializationCompleted`, mirroring `AvaDM.Console/Program.cs`'s style. `MainWindow` has no persistent nav chrome: `MainWindowViewModel` swaps a single `ContentControl` between the two page view models, each carrying its own way back/forward.

- **Downloads page** (`DownloadListViewModel`/`DownloadRowViewModel`) — toolbar with status filter tabs (All/Active/Completed/Failed) and a debounced text search; each row expands to show per-chunk progress and exposes pause/resume/cancel/remove; double-clicking a completed row opens the file or its containing folder (configurable). A row with no live handle for this process (e.g. after a restart) shows as a derived **Interrupted** status rather than a stale "Running" — downloads are not yet auto-resumed on startup, see Roadmap. Cancel and Remove each go through a confirmation overlay; non-terminal `DownloadHandle.LogMessage` events surface as toast notifications. A 1-second reconciliation poll keeps the list in sync with downloads started/finished elsewhere.
- **Settings page** (`SettingsViewModel`) — staged edits over the shared `DownloadSettings` (download dir, chunk count, retries, speed limit, repository path), plus UI-only preferences: appearance (dark/light), window-closing behavior (minimize to tray vs. close), completed-item double-click action, start-with-system (autostart), and a log-folder shortcut.
- **Tray icon** (`TrayIconService`) — click to show/hide the main window; native context menu lists in-progress downloads with live progress text and inline pause/resume, plus Exit. When "minimize to tray" is enabled, closing the main window hides it instead of exiting; the tray menu's own Exit always does a real shutdown.
- **Preferences persistence** (`UiPreferencesRepository`) — a small Dapper/`Microsoft.Data.Sqlite` key-value table in the *same* SQLite file as the download index, for UI-only prefs (theme, close-to-tray, double-click action). Start-with-system is the one exception: it isn't mirrored here, because the actual source of truth is the OS's own autostart entry (see below).
- **Autostart** (`AutoStartService`) — enables/disables login autostart via each OS's native mechanism directly (Windows `HKCU\...\Run` registry value, Linux `~/.config/autostart/*.desktop`, macOS `~/Library/LaunchAgents/*.plist`), reading that entry back live rather than trusting a cached flag. The registered launch command passes `--minimized`; `App.axaml.cs` hides the main window immediately after it opens when that flag is present, so a login-triggered launch starts hidden in the tray.
- **Crash handling** — `AppLogging` (Serilog rolling file log, see `LogDirectoryHint`/`OpenLogFolderCommand` in Settings) and `CrashReporter` (opens a pre-filled GitHub "new issue" page and reveals the log file) are wired as global exception handlers in `Program.cs` (`AppDomain`/`TaskScheduler`) and `App.axaml.cs` (`Dispatcher.UIThread.UnhandledException`).

## Console harness

The console app uses Terminal.Gui 2.x rather than the old hand-rolled cursor-positioning panel. It displays aggregate and per-chunk progress, logs, and an interactive command field. Supported commands include:

- `start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]`
- `pause <id>`, `resume <id>`, `cancel <id>`
- `speed <id> <bytesPerSec|off>`
- `setpath <dir>`, `status [id]`, and `quit`/`exit`

## Roadmap (not yet implemented — don't assume these exist)

- Automatic startup recovery/rehydration of persisted downloads (currently they show as "Interrupted" until manually restarted) and a first-class persisted queue.
- Retry-with-resume within a partially written chunk, dynamic chunk tuning, ETag/Last-Modified revalidation, and richer server/protocol support.
- Automated end-to-end coverage for network failures, disk errors, malformed resume data, and application restart scenarios — `test/AvaDM.Core.Tests` currently covers `DownloadManager` and `SpeedTracker` at a narrower scope.

## Notes

- Target platforms: Linux (Fedora) primary, Windows primary, macOS secondary via Avalonia.
- Full design doc: `docs/AvaDM-project-description.md`.
