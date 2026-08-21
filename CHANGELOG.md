# Changelog

All notable changes to AvaDM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Each released version below has a matching `vX.Y.Z` git tag. `.github/workflows/release.yml` reads
the section for the tag it is building and uses it as the GitHub Release notes, so **a release will
fail fast if its version has no section here**. Add the entry before pushing the tag.

## [Unreleased]

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

[Unreleased]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.2...HEAD
[1.1.2]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/AvaDM-org/AvaDM/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/AvaDM-org/AvaDM/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/AvaDM-org/AvaDM/releases/tag/v1.0.0
