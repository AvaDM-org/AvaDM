# AvaDM

Cross-platform (Linux/Windows, macOS later) open-source download manager in C#/.NET — a modern alternative to XDM/IDM/FDM. Name = **Ava**lonia (planned UI) + **DM**.

## Structure

- `src/AvaDM.Core` — download engine. `Downloader.cs` is the heart of it.
- `src/AvaDM.Console` — console test harness for exercising the engine during dev.
- `AvaDM.UI` *(planned)* — Avalonia desktop UI, not started.

## Engine mechanism

Chunked/resumable downloads over HTTP `Range` requests:
1. `HEAD` the URL for `Content-Length` and `Accept-Ranges: bytes`.
2. Pre-allocate the destination file, open one shared handle via `File.OpenHandle`, write with `RandomAccess.WriteAsync` at explicit offsets (avoids `FileStream`'s shared cursor — safe for concurrent non-overlapping writes).
3. Split the byte range into chunks, `GET` each with a `Range` header using `HttpCompletionOption.ResponseHeadersRead`, streaming bytes to disk rather than buffering.
4. No range support → fall back to a single sequential stream.

Currently in progress: `Downloader.Download(Uri uri)`, still sequential (chunks download one after another, not in parallel yet).

## Roadmap (not yet implemented — don't assume these exist)

- Parallel chunk downloads, retry-with-resume per chunk (Polly), dynamic chunk tuning, pause/resume via persisted chunk offsets, ETag/Last-Modified revalidation, multi-download queue.
- Avalonia UI: queue view, per-chunk progress, pause/resume/cancel, system tray.

## Notes

- Target platforms: Linux (Fedora) primary, Windows primary, macOS secondary via Avalonia.
- Full design doc: `docs/AvaDM-project-description.md`.
