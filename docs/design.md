---
version: "1.0.1"
# 1.0.1: dark palette's primary/primary-hover/primary-pressed shifted one step darker
# (values reused from the existing ramp, none invented) after the polish-pass contrast
# spot-check found on-primary-on-primary at ~3.47:1 in dark mode, below the ~4.5:1 floor
# this doc itself sets below. New dark on-primary/primary is ~5.37:1. Light palette was
# already passing (~5.38:1) and is unchanged.
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
    contents: "Add Download button (primary), search/filter field, status filter tabs"
  download-row:
    background: "surface, 1px border, card radius"
    contents: "file name, destination path, aggregate progress bar, status chip, speed + ETA in numeric-md, expand toggle for per-chunk detail"
  chunk-row:
    background: "surface-alt, control radius, shown when a download row is expanded"
    contents: "byte range (numeric-md), thin flat progress bar, per-chunk status"
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
---
# AvaDM Desktop UI

## Overview
Design reference for the planned `AvaDM.UI` Avalonia app: the desktop client for the AvaDM download manager described in the project's [`docs/AvaDM-project-description.md`](./AvaDM-project-description.md) and `CLAUDE.md`. Target screens: a main download list (queued/active/completed/failed, expandable per-chunk progress), an add-download dialog, and a settings page (default download folder, concurrency, speed limit, appearance). The tone is a dense, flat, utility tool — closer to a file manager or torrent client than a marketing dashboard. Ships dark by default; light is a first-class second built-in palette, not an afterthought.

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

Radius has exactly three values, used consistently: `control` (buttons, inputs, individual progress bars), `card` (download rows, settings group panels), `pill` (status chips only). Don't introduce other radii.

## Layout
Single window, no landing-page-style sections. Top: a toolbar (Add Download button, search/filter, status tabs — All / Active / Completed / Failed). Main area: a scrollable list of download rows; each row expands in place to reveal its chunk rows rather than opening a separate view. Settings is a separate page/dialog reachable from the toolbar, organized into bordered `panel-padding` groups (Downloads & Concurrency, Speed Limit, Storage/Repository Path, Appearance). Keep row height and padding stable across states — a row shouldn't visibly resize as its status or speed text changes.

## Components
See the `components` block in the frontmatter for the concrete spec of the toolbar, download row, chunk row, buttons, status chip, and progress bar. A few things worth calling out in prose:
- The aggregate progress bar on a download row and the thinner per-chunk progress bars in its expanded state share the same visual language (flat `primary` fill on a `surface-alt` track, `control` radius) so the relationship between them reads immediately.
- Status chips get their color from the semantic role of the download's current state, using the tinted-background + full-opacity-text pattern in the frontmatter — this keeps color meaningful without any gradient or glow.
- Buttons are flat: filled `primary` for the one primary action per screen (Add Download, Save Settings), outlined `secondary` for everything else, borderless `icon` buttons for row-level actions (pause/resume/cancel).

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

## Guardrails
- Flat fills only. No gradients, no drop shadows beyond the 1px `border`, no glow/blur.
- One accent (`primary`) for primary actions and focus rings — no second decorative accent.
- `success`/`warning`/`danger`/`info` are semantic, reserved for download state — never used decoratively elsewhere in the UI.
- Exactly three radii (`control`/`card`/`pill`), used consistently — don't invent a fourth.
- Motion stays subtle (100–200ms) and is never the point of a screen.
- Every visible color comes from a named token — no inline hex anywhere in views, so any palette (including a user's own) applies cleanly everywhere at once.
