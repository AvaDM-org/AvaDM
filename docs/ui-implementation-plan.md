# AvaDM.UI Implementation Plan

Status: **planning complete, implementation not started.** This is the resumable task
breakdown for building the Avalonia desktop client. Read [`design.md`](./design.md) first —
it is the binding visual spec; this document is the *how* and *in what order*, not a
re-statement of the *what it looks like*.

## Decisions locked in (from planning discussion)

1. **Settings is an in-window page**, not a separate `Window`/dialog — swapped into the
   main window via a simple content-switch, not the heavier `NavigationPage`/`TabbedPage`
   controls (this is a 2-page app: Downloads, Settings).
2. **Startup rehydration**: on-demand, not automatic-on-launch. See "Interrupted downloads"
   below — turns out this needs almost no new Core surface, just UI-side detection + reusing
   the existing `AddDownloadAsync(..., ConflictResolution.Resume)` path.
3. **Remove download**: removes the SQLite index row, with an optional "also delete the file
   from disk" toggle that must show an explicit warning before it's used. Needs one new
   `DownloadManager` method (Core change, small).
4. **Per-download controls (speed limit, etc.) live inline in the expanded row** — not a
   separate dialog. Matches design.md's existing chunk-detail expansion.
5. **MVVM stack: CommunityToolkit.Mvvm** (`[ObservableProperty]`, `[RelayCommand]`).
   **ReactiveUI is forbidden** (Avalonia expert rules).
6. Theming uses Avalonia's built-in `ResourceDictionary.ThemeDictionaries` keyed by
   `ThemeVariant.Dark`/`ThemeVariant.Light` — this is literally what design.md's "palette is a
   `ResourceDictionary` selected via `ThemeVariant`" contract maps onto natively. v1 ships
   exactly two palettes (Dark default, Light); the mechanism is built so a third custom
   palette is "add another `ThemeDictionaries` entry" later, not a rewrite. No palette
   import UI in v1 — that's future roadmap per design.md's own "implementation decision for
   whoever builds the import feature" note.
7. Theme choice (and any other UI-only preference) is **not** stored in `DownloadSettings`
   (that's a Core, transfer-related settings object) — but it still lives in SQLite, not a
   JSON file, for consistency with how the rest of the app persists state. Concretely: a new
   `UiPreferences` table in the **same** SQLite database file as the download index
   (`DownloadSettings.GetResolvedRepositoryPath()`), owned by a small `AvaDM.UI`-local
   repository class rather than by `AvaDM.Core` — one physical db file, but Core's
   `DownloadRepository` only ever touches `Downloads`, so Core stays UI-agnostic in code even
   though the file is shared.

## What "on-demand rehydration" actually requires

Investigated during planning: `DownloadManager.AddDownloadAsync` already resolves a
conflicting `(Uri, DestinationPath)` row with `ConflictResolution.Resume` by falling through
to `Downloader.StartDownload`, which itself detects and resumes from the `.avadm` footer.
**No deep Core surgery is needed for resume-after-restart** — the existing conflict/resume
plumbing already does it. The only real gap is UI-side:

- After `GetAllDownloadsAsync()`, any `DownloadRecord` whose state is `Running`/`Paused`/
  `Pending` but has `GetActiveHandle(id) == null` is *not actually live* in this process —
  display it as **Interrupted** (a derived UI-only status, not a new `DownloadState`) with a
  **Resume** action instead of Pause/Cancel controls.
- Clicking Resume on an Interrupted row calls
  `AddDownloadAsync(new Uri(record.Uri), record.DestinationPath, null, new ConflictResolution.Resume())`
  and wires up the returned handle's events exactly like a freshly-added download.
- `Failed`/`Cancelled` records also retain their `.avadm` file (per CLAUDE.md), so the same
  Resume action works for them too — no need for a separate "Retry" concept.
- **Optional, non-blocking Core nicety**: a thin `DownloadManager.ResumeDownloadAsync(Guid id)`
  wrapper that looks up the record and does the above, so the UI doesn't need to reconstruct
  a `Uri` from a stored string itself. Add only if it meaningfully cleans up the ViewModel —
  not a hard requirement.

## Core changes needed (small, do first)

- [ ] `DownloadManager.RemoveDownloadAsync(Guid id, bool deleteFile, CancellationToken ct = default)`
  - If `GetActiveHandle(id)` is non-null and still running/paused: cancel it first (await
    `handle.Completion` after calling `Cancel()`), then proceed. The UI's confirmation dialog
    is responsible for warning the user that removing an active download cancels it.
  - Deletes the SQLite row via `DownloadRepository.DeleteAsync` (already exists, just unused).
  - When `deleteFile` is true: delete `record.DestinationPath` if it exists, and also the
    `<destination>.avadm` sidecar if it exists (a downloaded-but-not-yet-finalized file only
    has the `.avadm` working file, not the final path — delete whichever is present).
  - Returns something UI can react to (e.g. `bool Success, string? Error`) — swallow-and-log
    is wrong here, the user needs to know if a file delete failed (permissions, in-use, etc.).
- [ ] (Optional, see above) `DownloadManager.ResumeDownloadAsync(Guid id)` convenience wrapper.
- [ ] Unit-test both against a temp SQLite file + temp directory, same pattern as any existing
  `DownloadRepository`/`DownloadManager` tests.

## Project scaffolding

- [ ] `dotnet new avalonia.app -o src/AvaDM.UI` (or minimal hand-rolled csproj matching the
  Core/Console style) — `net10.0`, `ImplicitUsings`, `Nullable` enabled, matching sibling
  projects' `.csproj` conventions.
- [ ] Add to `AvaDM.sln`.
- [ ] `ProjectReference` to `AvaDM.Core`.
- [ ] NuGet: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Diagnostics`
  (dev-only) or better, run the `migrate_diagnostics` MCP tool once the project exists to set
  up `AvaloniaUI.DiagnosticsSupport`/`AvaloniaUI.DeveloperTools` correctly instead of the
  deprecated package by hand. `CommunityToolkit.Mvvm`.
- [ ] `AvaloniaUseCompiledBindingsByDefault` = true in the csproj; every view gets an
  `x:DataType` set.
- [ ] Folders: `Views/`, `ViewModels/`, `Services/`, `Themes/`, `Converters/`, `Controls/`
  (for the custom `StatusChip` etc.).

## Theming infrastructure

- [ ] `Themes/Dark.axaml` and `Themes/Light.axaml`: `ResourceDictionary`s each defining every
  token key from design.md's `palette.dark`/`palette.light` frontmatter block as an Avalonia
  `Color`/`SolidColorBrush` resource, keyed under `ThemeDictionaries` on `ThemeVariant.Dark`/
  `ThemeVariant.Light` in `App.axaml`.
- [ ] All views reference tokens via `DynamicResource` (not `StaticResource` — must react to
  a runtime theme flip) — e.g. `Background="{DynamicResource SurfaceBrush}"`.
- [ ] Derived/semantic resources built once from tokens: e.g. status-chip backgrounds at
  "~15% opacity over current background" per design.md — implement as a fixed-opacity brush
  keyed per semantic color rather than recomputing per-view.
- [ ] `Styles/` for shared control styles: button variants (primary/secondary/icon), the
  `StatusChip` control (pill radius, tinted background/full-opacity text per state), the flat
  `progress-bar` style (track = surface-alt, fill = primary, control radius, no gradient).
  Use style **classes** + **pseudo-classes** for state (`:downloading`, `:paused`, `:failed`,
  `:completed`, `:queued`) — never `Triggers`.
- [ ] `UiPreferencesRepository` (in `AvaDM.UI`, not `AvaDM.Core`): opens the same SQLite file
  as `DownloadRepository` (via `settings.GetResolvedRepositoryPath()`) and owns one extra
  table it creates itself on first use, e.g. `CREATE TABLE IF NOT EXISTS UiPreferences (Key
  TEXT PRIMARY KEY, Value TEXT NOT NULL)` — a simple key/value store so future prefs (window
  size/position, last-selected filter tab, etc.) don't need schema changes. Holds at minimum
  a `ThemeVariant` key (`"Light"`/`"Dark"`). Load at startup, apply to
  `Application.Current!.RequestedThemeVariant`; a Settings-page toggle writes through it
  immediately (see Settings page below — this is the one setting that bypasses the staged/Save
  pattern).

## Composition root

- [ ] `App.axaml.cs`: construct one shared `HttpClient`, `DownloadSettings`, `DownloadManager`
  (mirroring `AvaDM.Console/Program.cs`'s wiring), plus `UiPreferences`. No heavy DI container
  needed at this scale — pass instances down through ViewModel constructors, or one small
  static/singleton service locator if that gets unwieldy.
- [ ] `MainWindowViewModel`: holds `CurrentPageViewModel` (either `DownloadListViewModel` or
  `SettingsViewModel`), a `NavigateToSettings`/`NavigateToDownloads` relay command pair. Main
  window XAML swaps content via a `ContentControl` + `DataTemplate`s (ViewLocator-by-convention
  pattern), not a full navigation framework.

## Downloads list page

- [ ] `DownloadRowViewModel`: wraps one `DownloadRecord` and (if live) its `DownloadHandle`.
  - Status: real `DownloadState`, plus the derived `Interrupted` display case described above.
  - Progress %, human-readable bytes/speed/ETA (formatting via converters, values always
    rendered in `numeric-md`/mono per design.md).
  - `ObservableCollection<ChunkRowViewModel>` populated from `handle.Chunks` /
    `ChunksChanged`, empty/hidden when not live or not expanded.
  - `IsExpanded` toggle.
  - Inline editable speed-limit field (visible only when expanded) bound to
    `handle.SetSpeedLimit`.
  - Relay commands: `Pause`, `Resume` (live) / `Resume` (interrupted → re-add via Resume
    resolution), `Cancel`, `Remove` (opens confirmation with the delete-file toggle+warning).
  - All `ProgressChanged`/`ChunksChanged`/`LogMessage` handlers marshal onto
    `Dispatcher.UIThread.Post` before touching observable properties.
- [ ] `DownloadListViewModel`: `ObservableCollection<DownloadRowViewModel>`, status filter
  (All/Active/Completed/Failed — matches design.md's toolbar tabs) + text search over
  filename/URL, reconciliation loop:
  - Periodic (~1s) `manager.GetAllDownloadsAsync()` merged by `Id` into the collection (add
    new rows, update non-live rows, never clobber a row whose live handle is already pushing
    faster updates via events).
  - New downloads added through the Add-Download flow are inserted immediately with their
    live handle, not waited-for via the poll loop.
- [ ] Views: `MainWindow.axaml` shell, `DownloadListView.axaml` (toolbar + items), 
  `DownloadRowView.axaml` (collapsed header row + expandable panel using design.md's
  `download-row`/`chunk-row` component specs exactly — same row height/padding across all
  states per the "row shouldn't visibly resize" guardrail), `Controls/StatusChip.axaml`.

## Add Download flow

- [ ] `AddDownloadViewModel`/view (in-window panel or lightweight popup, still no separate
  `Window`, consistent with decision #1's spirit): URL field, destination path field + native
  file/folder picker via Avalonia's `IStorageProvider`, an "Advanced" disclosure for chunk
  count / speed limit overrides.
- [ ] Submit flow: `CheckConflictAsync` first; no conflict → `AddDownloadAsync` directly and
  insert the new row into the list. Conflict → show inline Resume/Overwrite/Rename choice
  (same three options as the console's `--resume`/`--overwrite`/`--rename`) before proceeding.
- [ ] Errors surfaced inline, not via a dialog swallow.

## Remove-download flow

- [ ] Confirmation UI triggered from the row's Remove action: checkbox "Also delete the
  downloaded file from disk", with `danger`-token warning text that's specific about
  consequences (irreversible; if the download is still active it will be cancelled first).
  Wording adapts based on whether the row is currently live/active.
- [ ] Calls the new `DownloadManager.RemoveDownloadAsync`; on failure (e.g. file locked),
  shows the error rather than silently removing the index row anyway.

## Settings page

- [ ] `SettingsViewModel` staged-edit pattern (edit local copies, commit on explicit **Save
  Settings** primary button — matches design.md's "filled primary for the one primary action
  per screen... Save Settings" convention, rather than live-apply-per-keystroke):
  - Downloads & Concurrency: `DefaultDownloadDirectory` (+ folder picker), `DefaultChunkCount`.
  - Speed Limit: `DefaultSpeedLimitBytesPerSecond` (nullable — "no limit" state).
  - Storage: `RepositoryPath` (nullable — blank means platform default, show the resolved
    path via `GetResolvedRepositoryPath()` as placeholder/hint text).
  - Retry/backoff (worth exposing even if design.md doesn't call it out by name — it's part
    of "Downloads & Concurrency" conceptually): `DefaultMaxRetryAttempts`,
    `DefaultRetryBaseDelay`, `DefaultPerAttemptTimeout`.
  - Appearance: Light/Dark toggle, writes through `UiPreferences` and applies immediately
    (theme-switch is explicitly exempted from "Save to commit" — instant preview is expected
    UX for a theme toggle; only the Core-facing `DownloadSettings` fields batch under Save).
- [ ] Bordered `panel-padding` group `Border`s per design.md's Layout section, one per
  settings group listed above.

## Cross-cutting / polish (do alongside, not a separate late phase)

- [ ] Converters: bytes → human-readable (`B`/`KB`/`MB`/`GB`), bytes/sec → human-readable
  speed, ETA calculation from bytes-remaining/speed, byte-range formatting for chunk rows
  (reuse the `[start-end]` convention from `DownloadDashboard.cs`).
- [ ] `LogMessage` surfacing: no log panel in design.md's layout, so use a lightweight
  dismissible toast/snackbar for transient messages, plus persist the last error text on a
  `Failed` row (visible without expanding, since that's the actionable state).
- [ ] Run `migrate_diagnostics` MCP tool once the project exists so DevTools (F12) are wired
  correctly during development.
- [ ] Contrast-check both shipped palettes against design.md's ~4.5:1 floor once real brushes
  exist (spot check, not full automated a11y suite for v1).

## Deferred / explicit roadmap (not v1)

- Custom palette import/creation UI (mechanism is ready for it per the Theming section above;
  the actual import flow, file format, and validation UI are future work).
- System tray integration.
- Automated UI/ViewModel tests (unit-test the new `DownloadManager` methods now; broader
  ViewModel test coverage can follow once the shape settles).
- Persisting per-download `DownloadOptions` (chunk count override, etc.) across a resume —
  today a resumed download reconstructs its chunk layout from the `.avadm` footer regardless
  of what `DownloadOptions` is passed to `StartDownload` on resume, so this isn't a
  correctness gap, just a "did the user's original override survive" cosmetic nuance.

## Suggested build order

1. Core changes (`RemoveDownloadAsync` [+ optional `ResumeDownloadAsync`], tests).
2. Project scaffolding + theming infrastructure (get a themed empty window on screen first).
3. Downloads list page against **live** downloads only (skip persisted-record reconciliation
   initially) — proves the row/chunk-row/status-chip components against design.md.
4. Add Download flow (now the list has a way to get data into it).
5. Persisted-record reconciliation + Interrupted/Resume handling.
6. Remove-download flow.
7. Settings page.
8. Polish pass (converters, toasts, contrast check, DevTools wiring).
