# AvaDM

Cross-platform (Linux/Windows, macOS later) open-source download manager in C#/.NET — a modern alternative to XDM/IDM/FDM. Name = **Ava**lonia (planned UI) + **DM**.

## Structure

- `src/AvaDM.Core` — download engine and persistence. `Downloader.cs` is the transfer engine; `DownloadManager.cs` is the UI-agnostic orchestration layer.
- `src/AvaDM.Console` — Terminal.Gui console harness for exercising multiple downloads and their controls during development.
- `AvaDM.UI` *(planned)* — Avalonia desktop UI, not started.

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
- Polly retries transient connection errors, I/O errors, timeouts, HTTP 408/429/5xx responses, with exponential backoff and jitter. The default is five retries and a 30-second timeout per attempt.
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

## Console harness

The current console app uses Terminal.Gui 2.x rather than the old hand-rolled cursor-positioning panel. It displays aggregate and per-chunk progress, logs, and an interactive command field. Supported commands include:

- `start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]`
- `pause <id>`, `resume <id>`, `cancel <id>`
- `speed <id> <bytesPerSec|off>`
- `setpath <dir>`, `status [id]`, and `quit`/`exit`

## Roadmap (not yet implemented — don't assume these exist)

- Avalonia desktop UI: download queue view, per-chunk progress, pause/resume/cancel controls, settings, and system tray integration.
- Automatic startup recovery/rehydration of persisted downloads and a first-class persisted queue.
- Retry-with-resume within a partially written chunk, dynamic chunk tuning, ETag/Last-Modified revalidation, and richer server/protocol support.
- Automated end-to-end coverage for network failures, disk errors, malformed resume data, and application restart scenarios.

## Notes

- Target platforms: Linux (Fedora) primary, Windows primary, macOS secondary via Avalonia.
- Full design doc: `docs/AvaDM-project-description.md`.
