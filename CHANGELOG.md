# Changelog

All notable changes to AvaDM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each released version below has a matching `vX.Y.Z` git tag. `.github/workflows/release.yml` reads
the section for the tag it is building and uses it as the GitHub Release notes, so **a release will
fail fast if its version has no section here**. Add the entry before pushing the tag.

## [1.4.0] - Unreleased

_Accumulating several PRs; the release date and `v1.4.0` tag are set once the batch is complete._

### Changed

- **The Downloads page is now a file-explorer-style table.** ([#19](https://github.com/AvaDM-org/AvaDM/issues/19))
  - **Two toolbar bars.** The top one has the search box, a `+` button for the full Add Download
    dialog, a quick-add link box (with a clipboard-paste button inside it and a start button) that
    downloads a pasted link straight to the default folder, a trash button that appears when
    downloads are selected, and the settings gear. The second bar is the column header.
  - **Chosen columns.** Name (always shown) plus Type, Size, Created, Speed, Progress %, Progress
    size, and Status. Right-click the header to show/hide columns or move them; drag a header, or
    the grip on its edge, to reorder or resize; click a header to sort, click again to reverse.
    Your column choice and sort survive a restart.
  - **Select and bulk-remove.** A checkbox on every row plus click / Ctrl+click / Shift+click
    selection and a header select-all. The trash button or the Delete key removes everything
    selected at once, through one dialog that lists the files and has a single "also delete the
    files from disk" option.
  - **Right-click a download** for open file, open containing folder, copy download link, and
    remove.
  - **One progress bar per download, no expander.** Instead of expanding a row to see each
    connection, the single progress bar now fills unevenly — each connection fills its own part of
    it. The row shows the bar only while the download is active.
  - **The per-download speed limit moved into the Speed column** (it used to live in the expanded
    row): for a running download the Speed cell shows the current speed and the active limit, and
    clicking it opens the limit editor.
  - The old All/Active/Completed/Failed filter tabs are gone; sort and search replace them.
- **The Add Download form now has a separate "File name" field.** Typing a link fills it in with
  the name derived from the URL (the same name the engine would have used), and it stays editable,
  so the file can be saved under a different name than the link's without renaming it afterwards.
  "Save to" is now just the destination folder.
- **"Chunks" is now called "Connections" everywhere it's shown** (matching the term other
  download managers use): the per-connection rows in an expanded download ("Connection 1",
  "Connection 2", …) and that row's show/hide tooltip, the Add Download advanced options, and
  "Connections per download" in Settings. The Add Download field's placeholder now names the
  actual default it falls back to instead of just saying "Default".

## [1.3.1] - 2026-09-03

### Fixed

- **A login-started (autostart) instance left a blank, content-less window** on Linux - just a
  titlebar and frame with the desktop showing through, whose close button did nothing (only the
  tray menu's Exit could get rid of it), exactly the symptom [1.2.2](#122---2026-08-31) and the
  first attempt at this fix both failed to solve. The `--minimized` login launch showed the main
  window and then hid it again immediately; on Linux an Avalonia window hidden before it has ever
  rendered comes back from a later `Show()` permanently unpainted and unresponsive. A `--minimized`
  launch now never shows the window at all, so the first time it opens (from the tray) is a genuine
  first paint.

- **AvaDM running blocked the system from logging out, rebooting, or shutting down**, with the
  desktop reporting "Logout canceled by ''". With "minimize to tray" enabled (the default), the
  window's close handler cancelled *every* close request - including the one the OS session
  manager sends each window on logout. It now only redirects a genuine user close to the tray and
  lets a shutdown close the window normally.

## [1.3.0] - 2026-08-31

### Added

- **Downloads from servers that don't report a size now work instead of crashing.** A missing
  `Content-Length` header used to throw and surface as a startup crash log (#7). AvaDM now falls
  back to a single, non-resumable stream and shows the size as "???" - with an indeterminate
  progress bar instead of one stuck at 0% - until the download finishes, at which point the real
  size fills in.

### Fixed

- **A slow-but-steady download could be killed and restarted from scratch over and over, never
  completing**, if its total transfer time happened to exceed the retry timeout. That timeout used
  to bound an entire attempt's duration rather than how long it went quiet, which is fatal to a
  non-resumable download (the case above, or a server that doesn't support Range requests): every
  retry there restarts from byte 0, so it would hit the same wall at the same point forever. It's
  now a genuine inactivity timeout, reset on every byte received, so only an actually-stalled
  connection gets retried.

### Changed

- Settings > "Per-attempt timeout" is renamed to "Inactivity timeout" to match the fix above -
  `DownloadSettings.DefaultPerAttemptTimeout` is renamed to `DefaultInactivityTimeout`.

## [1.2.2] - 2026-08-31

### Fixed

- **A minimized autostart launch (or the tray icon's hide/show) could leave the main window a
  blank, inert taskbar/dock entry on Linux and macOS**, with nothing ever drawn once it came back
  from `Show()` - the only way to get rid of it was closing it from that entry's own menu. This
  works around an underlying Avalonia rendering bug
  ([AvaloniaUI/Avalonia#2994](https://github.com/AvaloniaUI/Avalonia/issues/2994),
  [#18148](https://github.com/AvaloniaUI/Avalonia/issues/18148)) by toggling `ShowInTaskbar`
  alongside every `Hide()`/`Show()` pair (autostart, the tray icon's manual toggle, and
  close-to-tray). Windows already hides the taskbar entry on its own, so the extra toggle is a
  no-op there.

## [1.2.1] - 2026-08-22

Fixes the download progress bar jitter reported in #10, plus two related consistency bugs found
along the way.

### Fixed

- **Chunk progress bars visually jittered back and forth during a download.** The chunk row's
  layout had the progress bar sharing width with an auto-sized speed-text column; since the
  formatted speed string's length genuinely varies ("-", "45.2 KB/s", "1.2 MB/s", ...), every such
  change resized that column and visually squeezed or expanded the bar next to it, indistinguishable
  from the bar's own value moving even though it never did. The speed column is now a fixed width.
- **Resuming a download briefly wiped its progress to zero before snapping back.** A freshly
  started handle is returned before its own HEAD request/`.avadm`-footer read has finished, so a
  resumed row could get attached to a still-zero, chunk-less handle and momentarily show 0%/no
  chunks before the real (already-substantial) progress arrived and corrected it.
- **A download that failed and auto-retried could leave its row permanently stuck**, showing the
  stale failed state indefinitely while the replacement download kept running invisibly in the
  background - and a user resuming that stuck row could end up racing a second handle against the
  first on the same `.avadm` file. The row now re-syncs once its handle reaches any terminal state,
  and starting a new attempt for an id that already has an active handle now cancels the old one
  first.
- **Concurrent chunk-download threads could report progress to the UI out of order**, occasionally
  delivering a smaller, stale byte total after a larger one had already gone out.

## [1.2.0] - 2026-08-21

Shows which version you are running.

### Added

- **Settings > Updates now shows the running build's version**, so it's clear which version you're
  on after a self-update (nothing else about the install changes visibly, and an AppImage or
  portable build keeps whatever filename it was originally downloaded under).

## [1.1.2] - 2026-08-21

Fixes login autostart and the Linux applications-menu shortcut for AppImage builds.

### Fixed

- **The autostart entry and the Linux applications-menu shortcut pointed into a temporary AppImage
  mount.** Both were written from `Environment.ProcessPath`, which for an AppImage is a path inside
  the `/tmp/.mount_*` FUSE mount that exists only while that process runs. After a reboot the
  desktop reported `Could not find the program '/tmp/.mount_.../usr/bin/AvaDM.UI'`, the shortcut
  did nothing, and AvaDM did not start at login. Both entries now point at the real `.AppImage`
  file (the same `APPIMAGE` resolution the updater already used), and the menu shortcut's icon is
  installed to `~/.local/share/icons/hicolor/256x256/apps/avadm.png` instead of being referenced
  inside that same temporary mount.
- **Existing broken entries repair themselves.** At startup AvaDM rewrites an autostart entry or
  menu shortcut that no longer matches where it actually lives — which also covers a portable build
  that was moved or an AppImage that was replaced at a new path. Entries the user turned off stay
  off.

## [1.1.1] - 2026-08-21

Repairs the auto-updater, which was broken or incomplete on four of the six distribution channels.

### Fixed

- **AppImage updates failed with `Read-only file system`.** The updater resolved its own location
  via `Environment.ProcessPath`, which for an AppImage points inside the temporary read-only FUSE
  mount the AppImage runtime unpacks itself into, rather than at the `.AppImage` file on disk. It
  now uses the `APPIMAGE` environment variable.
- **Linux portable (tar.gz) updates always failed.** The downloaded archive is gzipped, but was
  handed to a reader that only accepts an uncompressed tar stream, so extraction threw
  `EndOfStreamException` before anything was installed.
- **Linux updates left no running app.** The replacement build was launched while the old process
  still held the single-instance lock, so it treated itself as a duplicate launch and exited — and
  then the old process exited too. The relaunch now waits for the old process to terminate first.
- **Windows per-machine installs were treated as portable.** Channel detection only looked in
  `HKCU`; an installation into Program Files records itself in `HKLM`, so those builds were routed
  down the portable path and tried to overwrite Program Files without elevation.
- **The Windows installer never restarted AvaDM after a silent update.** The installer's launch
  entry is skipped during silent runs, so an update ended with the app closed and nothing to
  reopen it.
- **The Windows installer could not elevate for a per-machine update**, causing the update to fail
  on file copy. It is now told explicitly whether to request administrator rights.
- Update staging files are cleaned up when an update fails, instead of being left behind next to
  the application.

### Changed

- An update now fails with an explanatory message when the install directory is not writable,
  rather than exiting the app and never returning.
- Update *checks* time out after 30 seconds. Previously a hung request could leave "Check for
  Updates" disabled for the rest of the session. Update *downloads* remain unbounded.
- Skipped checksum verification (a release with no `SHA256SUMS.txt` entry for an asset) is now
  recorded in the log instead of passing silently.

### Known issues

- Because the bugs above live in the *shipped* updater, **1.1.0 users on the AppImage and Linux
  portable channels cannot reach this release through in-app update** and need to download it
  manually, once. Windows installer users may find the app does not reopen after updating;
  relaunching it manually completes the update. Later releases update normally on every channel.

## [1.1.0] - 2026-08-21

### Added

- Single-instance enforcement: launching AvaDM while it is already running now brings the existing
  window to the front instead of doing nothing.
- A GitHub link in Settings.

## [1.0.0] - 2026-08-21

First release.

### Added

- Segmented, multi-connection HTTP download engine with pause, resume, cancel, retry with
  exponential backoff, and per-download speed limiting.
- Resumable downloads across restarts via an `.avadm` working-file footer, and a SQLite index of
  download records.
- Avalonia desktop UI: downloads list with status filters, debounced search, per-chunk progress,
  and a settings page.
- System tray icon with per-download progress and inline pause/resume, plus optional
  minimize-to-tray.
- Start-with-system autostart and a Linux desktop shortcut.
- In-app auto-update against GitHub Releases, with SHA-256 verification of downloaded assets.
- Crash reporting and rolling file logs.
- Packaging for six formats: Windows installer and portable zip, Linux tar.gz, AppImage and `.deb`,
  and macOS `.dmg` (x64 and arm64).

[Unreleased]: https://github.com/AvaDM-org/AvaDM/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.2...v1.2.0
[1.1.2]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/AvaDM-org/AvaDM/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/AvaDM-org/AvaDM/releases/tag/v1.0.0
