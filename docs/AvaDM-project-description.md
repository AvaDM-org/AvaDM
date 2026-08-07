# AvaDM — Project Description

## Overview

AvaDM is a cross-platform, open-source download manager built in C#/.NET, targeting Linux (Fedora) and Windows. It's conceived as a modern alternative to tools like XDM, IDM, and Free Download Manager: fast, chunked/parallel downloads with a clean UI. The name comes from **Ava**lonia (the planned UI framework) + **DM** (Download Manager), following the XDM/IDM naming convention.

## Solution Structure

- **AvaDM.Console** — console entry point / test harness for exercising the core download engine during development.
- **AvaDM.Core** — the core download logic. Contains the `Downloader` class, which is the heart of the engine.
- **AvaDM.UI** *(planned)* — Avalonia-based desktop UI, not yet started.

## Core Download Engine (AvaDM.Core)

The download engine is built around HTTP `Range` requests to support chunked, resumable, and eventually parallel downloading.

**Mechanism:**
1. Send a `HEAD` request to the target URL to retrieve `Content-Length` (total file size) and check for an `Accept-Ranges: bytes` response header (partial-content support).
2. Pre-allocate the destination file and open a single shared file handle via `File.OpenHandle`, using `RandomAccess.WriteAsync` to write chunk data at explicit byte offsets. This avoids the shared mutable cursor problem of `FileStream` and is safe for concurrent, non-overlapping writes from multiple chunks.
3. Split the total byte range into chunks and issue `GET` requests per chunk, each with a `Range` header (`start`–`end`), reading `HttpCompletionOption.ResponseHeadersRead` streams and writing bytes as they arrive rather than buffering the full response.
4. If the server doesn't support ranged requests, fall back to a single-stream sequential download.

**Currently in progress:** the `Downloader.Download(Uri uri)` method in `AvaDM.Core`, implementing this core chunked-download logic.

## Roadmap

### Download engine
- Parallel chunk downloading (concurrent `Task`s writing to the shared file handle), building on the sequential version.
- Retry-with-resume per chunk: on a transient failure (connection reset, timeout, etc.), resume the chunk from its last-written offset rather than restarting it, using a resilience/retry pipeline (e.g. Polly) wrapped around the full request-read-write loop for each chunk — not just the initial request.
- Dynamic chunk count tuning: split remaining bytes further if a connection is slow, or merge/reduce parallelism if the server throttles per-connection.
- Pause/resume support: persist each chunk's `(start, currentOffset, end)` to a small metadata file so downloads can be paused and resumed across app restarts.
- `ETag` / `Last-Modified` validation to detect whether a resumed download's source file has changed since the download started.
- Download queue: support multiple downloads, queued and managed concurrently with configurable limits.

### UI (AvaDM.UI, Avalonia)
- Avalonia chosen for the UI layer: XAML-based (familiar if coming from WPF), genuinely cross-platform (Linux/Windows/macOS), with good built-in theming.
- Main window with a download queue/list view.
- Per-chunk progress visualization for active downloads.
- Pause/resume/cancel controls per download.
- System tray integration for background operation.

### Platform targets
- Primary: Linux (Fedora) and Windows.
- Secondary (via Avalonia): macOS.

## Tech Stack

- C# / .NET
- `System.Net.Http` (`HttpClient`, range requests)
- `RandomAccess` / `SafeFileHandle` for concurrent file writes
- Avalonia (planned, for UI)
- Polly / `Microsoft.Extensions.Http.Resilience` (planned, for retry/resilience logic)
