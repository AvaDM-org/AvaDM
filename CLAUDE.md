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
- **Autostart** (`AutoStartService`) — enables/disables login autostart via each OS's native mechanism directly (Windows `HKCU\...\Run` registry value, Linux `~/.config/autostart/*.desktop`, macOS `~/Library/LaunchAgents/*.plist`), reading that entry back live rather than trusting a cached flag. The registered launch command passes `--minimized`; `App.axaml.cs` hides the main window immediately after it opens when that flag is present, so a login-triggered launch starts hidden in the tray. `Program.cs` also handles a headless `--unregister-autostart` flag used by the Windows uninstaller (see Packaging below) to clear the Run entry on uninstall.
- **Linux desktop shortcut** (`DesktopShortcutService`) — separate from `AutoStartService`: writes/removes `~/.local/share/applications/avadm.desktop` (a normal, non-`--minimized` launch) so AvaDM shows up in the applications menu. Only relevant for the plain tar.gz portable Linux build; the `.deb` and AppImage packages install their own equivalent entry at package-install/integration time. Exposed as a Linux-only "Desktop Shortcut" card in Settings.
- **Auto-update** (`UpdateService`, `UpdateChannelDetector`) — checks `GET /repos/AvaDM-org/AvaDM/releases/latest` (silently on startup if Settings > Updates > "Check Automatically" is on, the default; always on demand via "Check for Updates") and, when the running build is older, offers to apply it. Always a *full* replacement of the previous build, never a binary diff. `UpdateChannelDetector` identifies which of release.yml's six distribution formats produced the running build (Windows: an Inno Setup uninstall registry key at a fixed AppId vs. its absence, checked in **both** HKCU and HKLM since only the hive distinguishes a per-user install from a Program Files one; Linux: the `APPIMAGE` env var, then a dpkg file-list check, then portable tar.gz as the fallback; macOS: always the one `.dmg` channel) purely from OS-native signals - release.yml doesn't need to know anything about this. For channels AvaDM's process fully owns its install location (both Windows channels, Linux portable, AppImage), `UpdateService` downloads the matching asset, verifies it against the release's `SHA256SUMS.txt`, and replaces files in place - same-directory atomic renames on Linux/AppImage (safe even while the old file is still open/running), a generated wait-then-`robocopy` PowerShell script on Windows portable (Windows won't let a running exe be overwritten directly), and a silent re-run of the Inno installer - then relaunches and exits. Three details are load-bearing and easy to regress:
    - **The AppImage's real path is `APPIMAGE`, not `Environment.ProcessPath`** — the latter points inside the read-only FUSE mount the AppImage runtime unpacks itself into, so writing there fails with `Read-only file system`.
    - **The relaunch must wait for this process to exit** before starting the new build (a detached `sh` waiter on Linux, `Wait-Process` in the Windows portable script). `SingleInstanceService` holds an exclusive lock for the whole process lifetime, so a replacement started too early loses that lock, treats itself as a duplicate launch, and exits — leaving nothing running once the old process then exits too.
    - **The Windows installer channel needs a relaunch handshake and an explicit privilege flag.** setup.iss's normal `[Run]` launch entry carries `skipifsilent` and so never fires on a silent update; a second `[Run]` entry gated on `/AVADMRELAUNCH=1` (passed by `UpdateService`, `runasoriginaluser` so an elevated update doesn't relaunch AvaDM as admin) covers that. And because `PrivilegesRequired=lowest` means Setup never elevates on its own, `UpdateService` passes `/ALLUSERS` for a per-machine install and `/CURRENTUSER` otherwise. Because the update never touches anything outside that install directory, and `DownloadSettings.RepositoryPath`'s database lives in the OS's per-user app-data directory instead (see below), an update can't lose downloads, history, or settings. For channels it doesn't own (the `.deb`, dpkg-tracked; the `.dmg`'s drag-installed `.app`), it defers to that platform's own update convention instead of mutating files outside its control - opening the package manager/release page for the `.deb`, or downloading and mounting the new `.dmg` in Finder for a manual drag-to-Applications.
- **Crash handling** — `AppLogging` (Serilog rolling file log, see `LogDirectoryHint`/`OpenLogFolderCommand` in Settings) and `CrashReporter` (opens a pre-filled GitHub "new issue" page and reveals the log file) are wired as global exception handlers in `Program.cs` (`AppDomain`/`TaskScheduler`) and `App.axaml.cs` (`Dispatcher.UIThread.UnhandledException`).

## Console harness

The console app uses Terminal.Gui 2.x rather than the old hand-rolled cursor-positioning panel. It displays aggregate and per-chunk progress, logs, and an interactive command field. Supported commands include:

- `start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]`
- `pause <id>`, `resume <id>`, `cancel <id>`
- `speed <id> <bytesPerSec|off>`
- `setpath <dir>`, `status [id]`, and `quit`/`exit`

## Packaging & CI

- `.github/workflows/release.yml` — the only workflow, by design (free-tier Actions minutes are limited, so there's no separate build-on-every-push CI). Fires only on version tags (`vX.Y.Z`). Builds all platforms in parallel and publishes a **draft** GitHub Release with every artifact attached (`gh release create ... --draft`); nothing is published live without a human clicking publish. Cut a release with `git tag vX.Y.Z && git push origin vX.Y.Z`.
  - **Windows** — self-contained single-file `win-x64` publish. Two artifacts: a portable zip of the publish output, and an installer built by **Inno Setup** (`packaging/windows/setup.iss`, installed via `choco install innosetup` on the runner — not preinstalled on current `windows-latest` images). The installer lets the user pick the install directory (`PrivilegesRequired=lowest` + default dir page, no forced admin/Program Files) and has an unchecked "create a desktop shortcut" task; its `[UninstallRun]` invokes the installed exe with `--unregister-autostart` before removing files, so uninstalling also clears any `HKCU\...\Run` entry `AutoStartService` wrote.
  - **Linux** — self-contained single-file `linux-x64` publish, packaged three ways: a portable tar.gz, an AppImage (`packaging/linux/avadm.desktop.template` + the published output as the AppDir, built with `appimagetool`), and a `.deb` (`packaging/linux/debian/` control template + postinst/postrm that refresh the desktop/icon caches, built with `dpkg-deb`).
  - **macOS** — self-contained single-file publish for both `osx-x64` and `osx-arm64` (two separate builds, not a universal binary), wrapped into a minimal `.app` bundle (`packaging/macos/Info.plist.template`, using the `avadm-logo.icns` asset) and then a `.dmg` via `hdiutil`. Unsigned/not notarized by default (no Apple Developer account yet) — users need to right-click → Open the first time to bypass Gatekeeper.
  - `AvaDM.UI.csproj` sets `InvariantGlobalization=true` so self-contained builds don't need the system's `libicu` (version-fragile across Linux distros, and AppImage has no apt to pull it from); the UI has no localized text yet, so the only user-visible effect is number/date formatting always using invariant (roughly en-US-like) conventions rather than the OS locale's.
  - The `publish-release` job also generates `SHA256SUMS.txt` over every artifact and publishes it alongside them - `UpdateService` (see Desktop UI above) downloads and checks it before applying a self-update.

## Roadmap (not yet implemented — don't assume these exist)

- Automatic startup recovery/rehydration of persisted downloads (currently they show as "Interrupted" until manually restarted) and a first-class persisted queue.
- Retry-with-resume within a partially written chunk, dynamic chunk tuning, ETag/Last-Modified revalidation, and richer server/protocol support.
- Code signing: Windows installer/exe signing (needs a code-signing cert) and macOS notarization (needs a paid Apple Developer account) — both currently ship unsigned.
- Automated end-to-end coverage for network failures, disk errors, malformed resume data, and application restart scenarios — `test/AvaDM.Core.Tests` currently covers `DownloadManager` and `SpeedTracker` at a narrower scope.

## Notes

- Target platforms: Linux (Fedora) primary, Windows primary, macOS secondary via Avalonia.
- Full design doc: `docs/AvaDM-project-description.md`.
