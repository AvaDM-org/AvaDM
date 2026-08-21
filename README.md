# AvaDM

**A modern, open-source download manager for Linux, Windows, and macOS.**

AvaDM is a fast, reliable, and feature-rich alternative to XDM, IDM, and FDM. Built with C#/.NET on the cutting-edge [Avalonia](https://avaloniaui.net) framework, it delivers concurrent multi-segment downloads, intelligent resumption, and a beautiful native desktop experience across all major platforms.

## Features

- **Multi-segment Downloads** — Split files into multiple concurrent chunks for faster downloads (with fallback to single-stream for non-compliant servers), you can choose the amount of segments in the settings
- **Intelligent Resumption** — Pause and resume downloads at will; automatically recover from connection failures with exponential backoff
- **Speed Control** — Real-time speed limiting to cap bandwidth consumption
- **Persistent Storage** — SQLite index automatically saves progress and metadata, surviving application restarts
- **Cross-Platform** — Native support for Linux, Windows, and macOS with platform-specific UI integrations
- **System Integration** — Autostart on login, tray icon with live download status, desktop shortcuts, and system notifications
- **Auto-Update** — Built-in update checker with safe in-place replacement
- **Crash Reporting** — Automatic error logging and GitHub issue pre-fill for quick troubleshooting

## Quick Start

### Download & Install

Visit the [Releases](https://github.com/AvaDM-org/AvaDM/releases) page for pre-built binaries:

- **Windows**: Portable ZIP or Inno Setup installer
- **Linux**: tar.gz, AppImage, or .deb package
- **macOS**: DMG (unsigned; right-click → Open to bypass Gatekeeper)

### From Source

```bash
# Clone the repository
git clone https://github.com/AvaDM-org/AvaDM.git
cd AvaDM

# Build and run the desktop UI
dotnet run --project src/AvaDM.UI

# Or the lightweight console interface
dotnet run --project src/AvaDM.Console
```

**Requirements:**
- .NET 10.0 SDK or later
- On Linux: GTK 3 development libraries (for Avalonia)

## Project Structure

- **`src/AvaDM.Core`** — Core download engine and SQLite persistence layer
- **`src/AvaDM.UI`** — Avalonia-based desktop application (primary user-facing interface)
- **`src/AvaDM.Console`** — Lightweight Terminal.Gui console harness for headless operation and testing
- **`test/AvaDM.Core.Tests`** — xUnit test suite for the download engine

## Architecture Highlights

### Download Engine

AvaDM's core (`Downloader.cs`) uses modern .NET patterns for efficient concurrent I/O:

1. **Smart Headers** — Sends `HEAD` requests to detect server capabilities and content length
2. **Parallel Chunks** — For range-capable servers, splits files into concurrent byte ranges with a shared speed limiter
3. **Pre-Allocation** — Writes directly to a `.avadm` working file using `File.OpenHandle` and `RandomAccess.WriteAsync` to avoid stream synchronization overhead
4. **Graceful Fallback** — Single-stream downloads for non-compliant servers
5. **Resilience** — Polly-based retry pipeline with exponential backoff for transient errors

### Persistence

- SQLite stores one record per `(URL, destination path)` in the platform data directory
- A binary footer in the `.avadm` file tracks chunk ranges, statuses, and byte counts
- Checkpoints occur every 5 seconds and on shutdown for safety
- Resumption is conflict-aware: stale or mismatched data triggers a safe fresh start

### Desktop UI (Avalonia)

- **Downloads Page** — Live progress per download and per chunk, with pause/resume/cancel controls
- **Settings Page** — Configure download directory, chunk count, retries, speed limits, and UI preferences
- **Tray Integration** — Quick access to active downloads and window control
- **Auto-Update** — Check and apply updates with automatic restart
- **Dark/Light Themes** — Seamless theme support

## Configuration

Most settings are available in the UI:

- **Download Directory** — Where completed files are saved
- **Chunk Count** — Number of parallel segments (1–n; default 5)
- **Retry Strategy** — Max attempts and backoff delay (default: 5 attempts, 30s per-attempt timeout)
- **Speed Limit** — Bytes per second (adjustable while downloading)
- **Repository Path** — Location of the SQLite metadata database (defaults to platform app-data directory)

UI-only preferences (theme, close-to-tray, autostart) are stored alongside the metadata.

## Console Interface

For headless or scriptable use, the Terminal.Gui console harness supports:

```
start <url> [destPath] [chunkCount] [--resume|--overwrite|--rename <path>]
pause <id>
resume <id>
cancel <id>
speed <id> <bytesPerSec|off>
status [id]
setpath <dir>
quit
```

## Building Releases

Releases are built and published via GitHub Actions on version tags:

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

The workflow builds Windows, Linux, and macOS artifacts in parallel:

- **Windows** — Self-contained zip and Inno Setup installer
- **Linux** — Portable tar.gz, AppImage, and .deb
- **macOS** — DMG with `.app` bundle for both x64 and ARM64

All artifacts are verified against `SHA256SUMS.txt` before in-place updates.

## Roadmap

Planned features (not yet implemented):

- Automatic download rehydration on startup
- Dynamic chunk tuning and retry-with-resume
- ETag/Last-Modified revalidation
- Richer protocol support (FTP, magnet, torrent)
- Code signing (Windows executables and macOS notarization)
- End-to-end test coverage for network failures and disk errors

## Development

### Running Tests

```bash
dotnet test
```

### Code Style

Follow standard C# conventions. The codebase uses:
- `async`/`await` for I/O-bound work
- MVVM (CommunityToolkit.Mvvm) for the UI layer
- Immutable event data and defensive copying

### Key Entry Points

- **UI**: `src/AvaDM.UI/App.axaml.cs` — Object graph wiring
- **Console**: `src/AvaDM.Console/Program.cs` — Terminal.Gui setup
- **Core**: `src/AvaDM.Core/DownloadManager.cs` — Orchestration
- **Transfer**: `src/AvaDM.Core/Downloader.cs` — HTTP and chunk logic

For detailed architecture, see [`docs/AvaDM-project-description.md`](docs/AvaDM-project-description.md).

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on opening issues, submitting pull requests, and setting up your development environment.

## License

AvaDM is open source and available under the [MIT License](LICENSE).

## Support

- **Report Bugs** — [GitHub Issues](https://github.com/AvaDM-org/AvaDM/issues)
- **Discuss Features** — [GitHub Discussions](https://github.com/AvaDM-org/AvaDM/discussions)
- **View Logs** — Settings > Log Folder (captures full Serilog output)

---

Built with [Avalonia](https://avaloniaui.net) and [.NET](https://dotnet.microsoft.com).
