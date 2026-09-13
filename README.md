# AvaDM

**A modern, open-source download manager for Linux, Windows, and macOS.**

AvaDM is a fast, reliable, easy-to-use alternative to XDM, IDM, and FDM. It downloads files faster by grabbing several pieces at once, can pause and resume downloads, and looks and feels like a native app on every major platform.

## Features

- **Downloads Files Faster** — AvaDM splits a file into several pieces and downloads them all at once, so it usually finishes much faster than a normal download. If a site doesn't support this, AvaDM just downloads the file the regular way instead
- **Pause, Resume, and Retry** — Pause a download and pick it up again later, even after closing and reopening AvaDM. If your internet connection drops partway through, AvaDM keeps trying on its own
- **Download Queue** — Add as many downloads as you like. AvaDM works on a limited number at a time and starts the next one automatically as soon as room opens up — you decide how many run at once
- **Schedule Downloads for Later** — Pick a date and time, and AvaDM will start the download for you automatically, even if you're not around when it happens
- **Speed Limit** — Set a maximum download speed so AvaDM doesn't slow down the rest of your internet
- **Remembers Everything** — Your downloads and their progress are saved automatically, so nothing is lost if you close AvaDM or restart your computer
- **Works Everywhere** — One app with the same look and features on Windows, Linux, and macOS
- **Runs Quietly in the Background** — Keep AvaDM in the system tray, see progress at a glance, and optionally have it start automatically when you log in
- **Updates Itself** — AvaDM can check for new versions and update itself with one click
- **Easy to Report Problems** — If something goes wrong, AvaDM helps you send a bug report with the details already filled in

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

- **Downloads Page** — Live progress per download and per chunk, with pause/resume/cancel controls, a concurrency-limited queue, and scheduled downloads
- **Settings Page** — Configure download directory, chunk count, max concurrent downloads, retries, speed limits, and UI preferences
- **Tray Integration** — Quick access to active downloads and window control
- **Auto-Update** — Check and apply updates with automatic restart
- **Dark/Light Themes** — Seamless theme support

## Settings

Everything below can be changed from the Settings page in the app:

- **Download Folder** — Where finished downloads are saved
- **Connections per Download** — How many pieces AvaDM splits a file into (default: 5)
- **Max Concurrent Downloads** — How many downloads can run at the same time; anything beyond that waits in a queue and starts automatically once a slot opens up (default: 10)
- **Retries** — How many times AvaDM retries a failed download, and how long it waits between tries
- **Speed Limit** — The fastest AvaDM is allowed to download, even while a download is already running
- **Resume on Startup** — Optionally have AvaDM automatically pick back up any unfinished downloads the next time it opens
- **Storage Location** — Where AvaDM keeps track of your download history (for advanced users)

Appearance (light/dark), minimize-to-tray, and autostart preferences are also saved automatically.

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
