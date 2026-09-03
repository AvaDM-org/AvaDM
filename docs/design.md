---
version: "1.1.0"
# 1.0.1: dark palette's primary/primary-hover/primary-pressed shifted one step darker
# (values reused from the existing ramp, none invented) after the polish-pass contrast
# spot-check found on-primary-on-primary at ~3.47:1 in dark mode, below the ~4.5:1 floor
# this doc itself sets below. New dark on-primary/primary is ~5.37:1. Light palette was
# already passing (~5.38:1) and is unchanged.
# 1.0.2: added the branded app icon (Assets/avadm-logo.ico + avadm-logo-{16..1024}.png),
# replacing the placeholder Assets/AvaDMTray.ico. Wired into ApplicationIcon, the
# MainWindow title-bar icon, and the tray icon.
# 1.1.0: Downloads page reworked into a file-explorer-style table (issue #19) - a two-bar
# toolbar (search + advanced-add + quick-add link box + selection trash + settings gear;
# then the column header), full-bleed selectable table rows with user-chosen reorderable
# resizable columns, and the per-connection detail folded into the one aggregate progress
# bar as uneven fill instead of a row expansion. Sketch: docs/redesign/.
name: "AvaDM Desktop UI"
description: "Design system for the AvaDM Avalonia desktop client: a flat, dense, utility-app look for a cross-platform download manager. Two built-in palettes (dark default, light) plus a token contract so users can create or import their own palette without touching layout or component code."
palette:
  dark:
    background: "#121417"
    surface: "#1B1F24"
    surface-alt: "#232830"
    border: "#2E3440"
    text-primary: "#E6E9EF"
    text-secondary: "#9AA4B2"
    text-disabled: "#5B6472"
    primary: "#2E6E89"
    primary-hover: "#3E8FB0"
    primary-pressed: "#234F62"
    on-primary: "#F5FAFC"
    success: "#4CAF7D"
    warning: "#D9A441"
    danger: "#D9534F"
    info: "#3E8FB0"
  light:
    background: "#F5F6F8"
    surface: "#FFFFFF"
    surface-alt: "#ECEFF3"
    border: "#D8DEE6"
    text-primary: "#1A1F26"
    text-secondary: "#5B6472"
    text-disabled: "#9AA4B2"
    primary: "#2E6E89"
    primary-hover: "#3E8FB0"
    primary-pressed: "#234F62"
    on-primary: "#F5FAFC"
    success: "#2F8F5B"
    warning: "#B5791F"
    danger: "#C1443F"
    info: "#2E6E89"
typography:
  ui:
    fontFamily: "platform default (unset FontFamily; let FluentTheme resolve Segoe UI / system sans / etc. per OS)"
  mono:
    fontFamily: "Cascadia Mono, Consolas, JetBrains Mono, DejaVu Sans Mono, monospace"
  heading-lg:
    fontSize: "20px"
    fontWeight: 600
    lineHeight: "1.2"
  heading-sm:
    fontSize: "15px"
    fontWeight: 600
    lineHeight: "1.3"
  body-md:
    fontSize: "13px"
    fontWeight: 400
    lineHeight: "1.4"
  label-sm:
    fontSize: "11px"
    fontWeight: 500
    lineHeight: "1.2"
    letterSpacing: "0.02em"
  numeric-md:
    fontFamily: "mono"
    fontSize: "13px"
    fontWeight: 500
    lineHeight: "1.2"
spacing:
  base: "4px"
  gap-xs: "4px"
  gap-sm: "8px"
  gap-md: "12px"
  gap-lg: "20px"
  row-padding: "8px 12px"
  panel-padding: "16px"
  toolbar-padding: "8px 16px"
rounded:
  control: "4px"
  card: "6px"
  pill: "9999px"
components:
  toolbar:
    background: "surface-alt, flat, 1px bottom border"
    contents: "two stacked bars. bar 1: search field, advanced-add (+) icon button, quick-add link box (clipboard-paste icon inside it, start button after it), a trash icon shown only while rows are selected, settings gear. bar 2: a select-all checkbox + the column header - one clickable cell per visible column"
  download-row:
    background: "transparent, 1px bottom border, full-bleed - a dense table row, not a card"
    contents: "select checkbox; name cell = file name + destination path (both ellipsized, each with a full-value tooltip) + the aggregate progress bar for an actively running/paused row only + a fixed-width strip of the five action icons (pause/resume/cancel/open-folder/remove) after the name; then one cell per visible trailing column"
    selection: "click selects, Ctrl+click toggles, Shift+click ranges, the checkbox toggles independently; selected rows use the primary color at low opacity (SelectionBrush), same tint idea as the status chips"
  columns:
    options: "Name (pinned leftmost, never hidden or moved), Type (file extension), Size, Created (yyyy-MM-dd HH:mm local), Speed, Progress %, Progress size (both carry time-remaining as a secondary line), Status (the status chip; hidden by default)"
    behaviour: "click a header to sort by it, click again to flip asc/desc (arrow glyph on the active column); drag a header or use its right-click Move left/right to reorder; drag the right-edge grip to resize; right-click for the show/hide + move menu"
    persistence: "column order, visibility, and the sort column/direction persist (UiPreferences JSON); widths reset to defaults each launch"
  aggregate-progress-bar:
    background: "surface-alt track, control radius, shown only for an active (running/paused) row"
    fill: "one flat primary segment per connection, each sized to that connection's byte range and filled by its own progress, no divider - the bar fills unevenly; a single indeterminate bar when the size is unknown"
  button:
    primary: "filled with primary, on-primary text, control radius, no gradient"
    secondary: "surface background, 1px border, text-primary text, control radius"
    icon: "transparent background, surface-alt on hover, no border"
  status-chip:
    shape: "pill radius"
    background: "semantic color at ~15% opacity over the current background"
    text: "full-opacity semantic color"
  progress-bar:
    track: "surface-alt"
    fill: "primary (aggregate) — flat fill, no gradient, no shimmer"
    radius: "control"
assets:
  logo:
    files: "Assets/avadm-logo.ico, Assets/avadm-logo-{16,32,48,64,128,256,512,1024}.png"
    usage: "Windows exe icon (ApplicationIcon), MainWindow title-bar/taskbar icon, tray icon — all via the .ico. The individual PNG sizes are held for uses the .ico doesn't cover: a Linux .desktop icon, packaging/installers, an About dialog, and store/marketing listings."
---
# AvaDM Desktop UI

## Overview
Design reference for the `AvaDM.UI` Avalonia app: the desktop client for the AvaDM download manager described in the project's [`docs/AvaDM-project-description.md`](./AvaDM-project-description.md) and `CLAUDE.md`. Target screens: a file-explorer-style download table (see Layout), an add-download dialog, and a settings page (default download folder, concurrency, speed limit, appearance). The tone is a dense, flat, utility tool — closer to a file manager or torrent client than a marketing dashboard. Ships dark by default; light is a first-class second built-in palette, not an afterthought.

## Colors
Every color used in a view must come from one of the named tokens above (`background`, `surface`, `surface-alt`, `border`, `text-primary`, `text-secondary`, `text-disabled`, `primary` + its `-hover`/`-pressed` states, `on-primary`, `success`, `warning`, `danger`, `info`) — never an inline hex value in XAML. That's what makes the palette swappable; see **Theming & Palettes** below.

- `background` / `surface` / `surface-alt` are flat fills only — no gradients, no blur, no drop shadows. A 1px `border` is the only separation device between a row/card and its background.
- `primary` is the single accent color: used for the primary action button, active/focused states, focus rings, and the "downloading" status (it doubles as `info`). Don't introduce a second decorative accent.
- `success` / `warning` / `danger` are reserved for download state only:
  - `success` → completed
  - `info` (= `primary`) → downloading
  - `warning` → paused
  - `danger` → failed
  - queued / cancelled use `text-secondary` on `surface-alt` (neutral, no semantic color) rather than a fifth hue.

## Typography
No web fonts. UI text uses the OS default (leave `FontFamily` unset on base styles so Avalonia's FluentTheme resolves the platform's native system font — Segoe UI on Windows, the distro default on Linux). This can change later if we decide to bundle a font (e.g. Inter) for pixel-identical cross-platform rendering, but for now: native, zero embedding.

Numeric/tabular data — transfer speed, file size, ETA, percentage, chunk byte ranges — always uses the `mono` fallback stack (`numeric-md`) so digits align in columns and don't jitter as values update. Everything else (labels, filenames, descriptions, buttons) uses the `ui` family at the scale that fits: `heading-lg` for page titles ("Downloads", "Settings"), `heading-sm` for section headers within a page, `body-md` as the default row/label text, `label-sm` for status chips and column headers.

## Spacing & Radius
4px base grid — tighter than a typical web layout because this is an information-dense list UI, not a marketing page. `gap-sm`/`gap-md` separate elements within and between rows; `panel-padding` is for settings groups and dialogs; `toolbar-padding` for the top bar.

Radius has exactly three values, used consistently: `control` (buttons, inputs, individual progress bars), `card` (settings group panels), `pill` (status chips only). Don't introduce other radii. (Download rows are full-bleed table rows now — no radius; see Layout.)

## Layout
Single window, no landing-page-style sections. The Downloads page (reworked in issue #19 — sketch in [`redesign/`](./redesign/)) is a **file-explorer-style table**:

- A two-bar toolbar. Bar 1: a search field, an advanced-add `+` button (opens the Add Download dialog), a quick-add link box with a clipboard-paste icon inside it and a start button after it, a trash icon that appears only while rows are selected, and the settings gear. Bar 2: a select-all checkbox plus the column header.
- Full-bleed rows separated by a 1px bottom border (no card, no gap). Each row has a select checkbox and the name cell (name + path + — for an active row — the aggregate progress bar, then the five action icons); the remaining cells are the user's chosen columns.
- Columns (Name pinned; Type/Size/Created/Speed/Progress %/Progress size/Status optional) can be shown/hidden, reordered (drag or right-click menu), resized (edge grip), and sorted (click a header, click again to flip). Order/visibility/sort persist; widths don't.
- Rows are multi-selectable (click / Ctrl+click / Shift+click / checkbox / select-all); the trash icon or the Delete key removes the whole selection through one confirmation that lists the names.
- The per-connection detail is **not** a row expansion any more — the single aggregate bar fills unevenly, one segment per connection.

Row height is no longer forced constant: an active row is taller (it shows the progress bar) than an idle one. Keep everything *else* about a row stable as its text updates.

Settings is a separate page reachable from the gear, organized into bordered `panel-padding` groups (Downloads & Concurrency, Speed Limit, Storage/Repository Path, Appearance).

## Components
See the `components` block in the frontmatter for the concrete spec of the toolbar, download row, columns, aggregate progress bar, buttons, and status chip. A few things worth calling out in prose:
- The aggregate progress bar's per-connection segments use the same flat `primary` fill on a `surface-alt` track (`control` radius) the bar always had — the only change is that it's now divided into per-connection regions that fill independently.
- Status chips get their color from the semantic role of the download's current state, using the tinted-background + full-opacity-text pattern in the frontmatter. The Status column is off by default; when hidden, state is read from the progress bar, the failed-row error line, and which action icons show.
- A selected row's background is `primary` at low opacity (`SelectionBrush`) — the same tinting idea as the chips, so selection reads as "primary" without a second accent.
- Buttons are flat: filled `primary` for the one primary action per screen (Add Download, Save Settings), outlined `secondary` for everything else, borderless `icon` buttons for toolbar and row-level actions.

## Motion
Kept intentionally minor — this is a utility app, not a showcase. Only allowed motion: ~100–200ms ease transitions for hover/pressed background changes, focus-ring appearance, theme switch (dark↔light), and progress-bar fill interpolation as a download's byte count updates. No page-transition choreography, no staggered/scroll-triggered reveals, no ambient or particle effects, nothing that competes with the download data itself for attention.

## Theming & Palettes
This is the part that matters most beyond "look good": the UI must never hardcode a color, so a palette can be swapped — including a user-authored or imported one — without touching any view.

- **Token contract.** A palette is exactly the key set shown under `palette.dark` / `palette.light` above: `background`, `surface`, `surface-alt`, `border`, `text-primary`, `text-secondary`, `text-disabled`, `primary`, `primary-hover`, `primary-pressed`, `on-primary`, `success`, `warning`, `danger`, `info`. Any palette (built-in or user-supplied) must define all of these.
- **Mechanism.** Maps directly onto Avalonia's theming: each palette is a `ResourceDictionary` supplying those keys, selected via `ThemeVariant` (Dark/Light) or as a named alternate. The two palettes above are the built-in Dark (default) and Light dictionaries; "import/create a palette" in Settings is just adding another dictionary with the same keys and switching to it at runtime.
- **Reserved meaning.** `success`/`warning`/`danger`/`info` may change hue in a custom palette but must keep mapping to the same download states (completed/paused/failed/downloading) — a palette can't repurpose "success green" to mean something else, or status chips stop being legible at a glance.
- **Fallback, not failure.** If an imported palette is missing a key, the app should fall back to the built-in Dark value for that key rather than rendering unstyled or crashing — validate the full key set on import and warn the user about anything missing, don't silently accept a partial palette either.
- **Contrast floor.** Whatever the hues, `text-primary` on `background`/`surface` and `on-primary` on `primary` should stay at roughly 4.5:1 contrast or better. Worth a validation check in the palette import UI later so a bad custom palette doesn't ship a screen nobody can read.
- File format for a palette (JSON vs. an `.axaml` resource dictionary vs. something else) is an implementation decision for whoever builds the import feature — not fixed here. What's fixed is the key set and the semantic contract above.

## Branding & App Icon
The app icon is `Assets/avadm-logo.ico` (multi-resolution) plus standalone `Assets/avadm-logo-{16,32,48,64,128,256,512,1024}.png` raster exports at each size. The `.ico` is wired in three places: `AvaDM.UI.csproj`'s `ApplicationIcon` (the compiled exe's icon on Windows), `MainWindow`'s `Icon` attribute (title bar and taskbar/dock), and `App.axaml`'s `TrayIcon` (system tray). All Assets files are pulled in as Avalonia resources via a single `Assets/**` glob rather than listing them one by one, so a future asset just needs to be dropped in the folder. The standalone PNGs aren't referenced from XAML yet — they exist for icon needs outside Avalonia's resource system (a Linux `.desktop` file, OS packaging/installers, an About dialog, store listings) and should be reached for there instead of re-deriving a raster from the `.ico`.

## Guardrails
- Flat fills only. No gradients, no drop shadows beyond the 1px `border`, no glow/blur.
- One accent (`primary`) for primary actions and focus rings — no second decorative accent.
- `success`/`warning`/`danger`/`info` are semantic, reserved for download state — never used decoratively elsewhere in the UI.
- Exactly three radii (`control`/`card`/`pill`), used consistently — don't invent a fourth.
- Motion stays subtle (100–200ms) and is never the point of a screen.
- Every visible color comes from a named token — no inline hex anywhere in views, so any palette (including a user's own) applies cleanly everywhere at once.
