# AvaDM

**A modern, open-source download manager for Windows, Linux, and macOS.**

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

## Download & Install

Grab the latest version from the [Releases](https://github.com/AvaDM-org/AvaDM/releases) page:

- **Windows** — Portable ZIP, or an installer if you'd rather AvaDM set itself up for you
- **Linux** — tar.gz, AppImage, or a .deb package, whichever fits how you install software
- **macOS** — DMG. It isn't signed yet, so the first time you open it, right-click the app and choose "Open" to let it run

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

## Getting Help

- **Found a bug?** [Open an issue](https://github.com/AvaDM-org/AvaDM/issues) — AvaDM can pre-fill most of the details for you if it crashes
- **Have an idea or a question?** [Start a discussion](https://github.com/AvaDM-org/AvaDM/discussions)
- **Need the logs for a bug report?** Settings > Log Folder

## Contributing

Want to help build AvaDM? See [CONTRIBUTING.md](CONTRIBUTING.md) for how to set up a development environment, the codebase layout, and how the project's pull requests and releases work.

## License

AvaDM is open source and available under the [MIT License](LICENSE).

---

Built with [Avalonia](https://avaloniaui.net) and [.NET](https://dotnet.microsoft.com).
