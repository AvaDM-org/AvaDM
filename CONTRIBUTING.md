# Contributing to AvaDM

Thank you for your interest in contributing to AvaDM! We welcome bug reports, feature requests, and pull requests from the community. This guide will help you get started.

## Code of Conduct

We are committed to providing a welcoming and inclusive environment. All contributors are expected to:

- Be respectful and constructive in communications
- Welcome diverse perspectives and backgrounds
- Focus on what is best for the community
- Show empathy and consideration for other contributors

Instances of abusive, harassing, or unacceptable behavior can be reported by contacting the project maintainers.

## Getting Started

### Prerequisites

- .NET 10.0 SDK or later
- A code editor (VS Code, Rider, Visual Studio, etc.)
- On Linux: GTK 3 development libraries

### Development Setup

1. **Fork and clone** the repository:
   ```bash
   git clone https://github.com/YOUR-USERNAME/AvaDM.git
   cd AvaDM
   ```

2. **Build the project**:
   ```bash
   dotnet build
   ```

3. **Run tests** to verify the setup:
   ```bash
   dotnet test
   ```

4. **Start developing** — branch off an up-to-date `master`, named `<type>/<issue-number>-<short-slug>` (`<type>` matches the commit prefix below, e.g. `fix` or `feat`):
   ```bash
   git checkout -b fix/123-download-bar-jitter
   ```

## Reporting Bugs

If you find a bug, please open a GitHub Issue with:

- **Clear title** — What is broken?
- **Description** — What did you do? What happened? What should have happened?
- **Environment** — OS, .NET version, build method (installer, portable, source)
- **Logs** — Attach relevant logs from Settings > Log Folder
- **Screenshots** — If applicable, UI-related bugs benefit from visual context
- **Reproduction steps** — Minimal steps to reproduce the issue

### Before Reporting

- Check if the bug is already reported in [Issues](https://github.com/AvaDM-org/AvaDM/issues)
- Try the latest build; the bug may already be fixed
- Test with a fresh `.avadm` folder to rule out corrupted metadata

## Suggesting Features

Feature requests are welcome! Please open an Issue with:

- **Clear title** — What feature would you like?
- **Motivation** — Why do you need it? What problem does it solve?
- **Alternatives** — Have you considered other solutions?
- **Examples** — If available, reference similar features in other tools

## Submitting Pull Requests

### Before Starting

1. **Check existing PRs** — Avoid duplicate work by searching [Pull Requests](https://github.com/AvaDM-org/AvaDM/pulls)
2. **Open an issue first** — For non-trivial changes, discuss your approach in an Issue to avoid wasted effort
3. **Assign yourself** — Comment on the Issue to indicate you're working on it

### Pull Request Process

1. **Code quality** — Follow the existing code style:
   - Use C# naming conventions (PascalCase for public members, camelCase for locals)
   - Keep methods focused and testable
   - Avoid deep nesting; prefer early returns
   - Use `async`/`await` for I/O-bound operations

2. **Tests** — Add xUnit tests for new functionality or bug fixes in `test/AvaDM.Core.Tests`:
   ```csharp
   [Fact]
   public async Task DownloadHandle_Pause_StopsChunkTasks()
   {
       // Arrange
       var handle = /* create test handle */;
       
       // Act
       handle.Pause();
       
       // Assert
       Assert.Equal(DownloadState.Paused, handle.State);
   }
   ```

3. **Commit messages** — Follow [Conventional Commits](https://www.conventionalcommits.org/):
   - Format: `type(scope): subject`, e.g. `fix(core): ...`, `feat(ui): ...`, `test(core): ...`, `docs: ...`
   - Common types: `feat`, `fix`, `test`, `docs`, `refactor`, `chore`; scope is usually the touched
     area (`core`, `ui`, `console`) and can be omitted for repo-wide changes like `docs:`
   - Subject in imperative mood ("add" not "added"), first line under ~72 characters
   - Body explains the why, not just the what — especially for a non-obvious root cause

   Example:
   ```
   fix(core): serialize progress-report snapshotting to stop bar jitter

   Concurrent chunk tasks could each invoke ProgressChanged on their own
   thread with no ordering guarantee, so a stale, smaller snapshot could
   reach the UI after a larger one already had. Fixes #10.
   ```

4. **Keep commits focused** — One logical step per commit rather than one giant commit; for
   example, a fix and its regression test are typically two commits, not one. Avoid mixing
   refactoring with bug fixes.

5. **Open the PR**:
   - Target `master`, from your `<type>/<issue-number>-<short-slug>` branch
   - Link the issue: "Fixes #123"
   - Describe the change and testing approach
   - If visual changes, include a screenshot or screen recording
   - Ensure CI passes (builds and tests)

### What We Look For

- **Correctness** — Does it fix the issue or implement the feature correctly?
- **Testing** — Are edge cases covered? Do tests fail before the fix?
- **Performance** — Does it maintain or improve download speed and memory usage?
- **Safety** — No data loss risks; graceful handling of corrupted metadata
- **Documentation** — Code is self-explanatory; complex logic has comments explaining the why

### What We Don't Accept

- **Incomplete work** — Don't open PRs for work-in-progress; use draft PRs if you need feedback early
- **Style-only changes** — Whitespace, formatting, or renaming without functional improvements
- **Unrelated changes** — Keep PRs focused; separate unrelated fixes into different PRs
- **Breaking changes without discussion** — Coordinate API changes in an Issue first

## Code Review

All PRs receive review from maintainers. We may request changes to:

- Improve code clarity or maintainability
- Align with project conventions
- Address edge cases or error handling
- Reduce allocations or improve performance

Feedback is constructive; please don't take suggestions personally. We're all working toward a better AvaDM!

## Areas to Contribute

- **Core engine** — Download logic, resilience, chunk management (`src/AvaDM.Core`)
- **Desktop UI** — New settings, improved layouts, theme enhancements (`src/AvaDM.UI`)
- **Console interface** — New commands or better Terminal.Gui integration (`src/AvaDM.Console`)
- **Testing** — Expanding test coverage, especially around edge cases
- **Documentation** — Clarifying README, code comments, or design docs
- **Packaging** — Improving Windows installer, Linux AppImage, macOS `.dmg`
- **Localization** — Translating UI text or documentation (when supported)
- **Bug fixes** — Any open issue marked [help wanted](https://github.com/AvaDM-org/AvaDM/issues?q=label%3A%22help+wanted%22)

## Project Structure

Understanding the codebase:

- **`src/AvaDM.Core/Downloader.cs`** — HTTP transfer engine; handles chunking, ranges, retries
- **`src/AvaDM.Core/DownloadManager.cs`** — Orchestration layer; manages SQLite, active handles, conflict resolution
- **`src/AvaDM.Core/DownloadHandle.cs`** — Public API; exposes state, events, and controls to UI/console
- **`src/AvaDM.UI/MainWindow.xaml.cs`** — Avalonia main window; coordinates view models
- **`src/AvaDM.UI/ViewModels/DownloadListViewModel.cs`** — Downloads list logic
- **`src/AvaDM.UI/Services/`** — Tray integration, autostart, update checking, crash reporting

For more details, see [`docs/AvaDM-project-description.md`](docs/AvaDM-project-description.md).

## Testing Guidelines

### Running Tests

```bash
# Run all tests
dotnet test

# Run tests for a specific project
dotnet test test/AvaDM.Core.Tests

# Run a specific test
dotnet test --filter "SpeedTrackerTests"

# Run with verbose output
dotnet test --verbosity detailed
```

### Writing Tests

- **Arrange-Act-Assert** pattern — Set up state, perform action, verify result
- **Descriptive names** — `DownloadHandle_Pause_StopsChunkTasks` is better than `TestPause`
- **One assertion per test** — Easier to debug; use multiple tests if needed
- **Avoid mocking** — Prefer integration tests using real SQLite and files
- **Test edge cases** — Corruption, network errors, concurrent operations

Example:
```csharp
[Fact]
public async Task DownloadManager_ResumeWithMismatchedSize_StartsOver()
{
    // Arrange
    var manager = new DownloadManager(testDbPath);
    var url = "https://example.com/file.iso";
    var dest = Path.Combine(tempDir, "file.iso");
    
    // Initial download (corrupted footer with wrong size)
    var handle1 = await manager.StartDownloadAsync(url, dest, options);
    await handle1.CompletionTask;
    
    // Resume attempt with mismatched size
    var handle2 = manager.StartDownloadAsync(url, dest, 
        new() { ConflictResolution = ConflictResolution.Resume });
    
    // Assert: new download starts fresh, not resumed
    Assert.NotEqual(handle1.Id, handle2.Id);
}
```

## Debugging

### Enable Detailed Logging

Set the `AVADM_DEBUG` environment variable to capture full Serilog output:

```bash
export AVADM_DEBUG=1
dotnet run --project src/AvaDM.UI
```

Check `~/.local/share/AvaDM/logs/` (Linux) or the equivalent Windows/macOS app-data directory.

### Attach a Debugger

In VS Code:
```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "AvaDM.UI",
      "type": "coreclr",
      "request": "launch",
      "program": "${workspaceFolder}/src/AvaDM.UI/bin/Debug/net10.0/AvaDM.UI.dll",
      "cwd": "${workspaceFolder}",
      "stopAtEntry": false
    }
  ]
}
```

## Release Process

> **Note:** This is for maintainers only. Contributors don't need to follow this.

Releases are triggered by version tags. First, add a `## [X.Y.Z] - YYYY-MM-DD` section to
[`CHANGELOG.md`](CHANGELOG.md) (with `### Added`/`### Fixed`/`### Changed` as applicable) and merge
it to `master` — `release.yml` reads that section as the GitHub Release notes and **fails fast if
the tag's version has no matching entry**. Then tag and push:

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

GitHub Actions builds all platforms, generates checksums, and publishes a draft release. A maintainer reviews and clicks "Publish" to make it live.

## Getting Help

- **Questions about the codebase?** Open a GitHub Discussion or comment on a related Issue
- **Stuck on a bug?** Describe your issue in detail; maintainers may offer guidance
- **Want to pair?** Reach out via an Issue; we're happy to collaborate

## License

By contributing, you agree that your code will be licensed under the [MIT License](LICENSE). We don't require contributor agreements; your contributions remain yours to use elsewhere.

---

Thank you for making AvaDM better! We're excited to work with you.
