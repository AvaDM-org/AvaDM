using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvaDM.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.Services;

/// <summary>
/// Owns the system tray icon's behavior: restoring the window on click, building/rebuilding its
/// context menu, and intercepting the main window's close so it hides instead of exiting when
/// Settings > Window Closing is set to "Minimize to Tray" (the default).
///
/// The context menu is built imperatively - <see cref="NativeMenu"/> has no data-binding, only
/// an <c>Items</c> list and a <see cref="NativeMenu.NeedsUpdate"/> event fired right before the
/// menu is shown - so <see cref="RebuildMenu"/> re-populates it fresh rather than keeping it
/// continuously in sync. It's called from three places:
///   1. Eagerly from this constructor, covering the first popup on Linux dbusmenu hosts
///      (GNOME's AppIndicator/KStatusNotifierItem extension in particular) that don't reliably
///      raise NeedsUpdate before that first right-click.
///   2. On <see cref="NativeMenu.NeedsUpdate"/>, for hosts that do honor it.
///   3. On <see cref="DownloadListViewModel.DownloadsChanged"/>, which fires whenever a row is
///      added/removed or changes status (start, pause, resume, complete, fail) - this is what
///      keeps a since-opened menu's Pause/Resume submenus and status text from going stale after
///      an in-menu action, now that NeedsUpdate alone has proven unreliable for that on Linux. A
///      1-second poll timer was tried first for this instead and reverted: it rebuilt
///      <c>Items</c> unconditionally, including while the popup could be open, which visibly
///      reset/glitched the menu every tick. Event-driven rebuilding only touches the menu when
///      something tray-relevant actually changed, so it doesn't have that failure mode.
///
/// Per-download progress *percentage* text is kept live via <see cref="_liveUpdateTimer"/>,
/// which runs continuously (not gated to while the popup is open - see the constructor's doc
/// comment on why) and pushes plain <c>Header</c> property updates onto the existing items,
/// rather than going through another <see cref="RebuildMenu"/> call (which would reintroduce
/// the reverted 1-second poll timer's glitch). See <see cref="UpdateLiveProgress"/>.
///
/// Even a pure <c>Header</c> update - no <c>Items.Clear()</c>, no <see cref="RebuildMenu"/> -
/// still visibly flickers on Linux dbusmenu hosts if it's pushed every tick regardless of
/// whether the text actually changed. That's not an Avalonia-side rebuild: setting
/// <c>Header</c> makes the exporter emit a dbusmenu <c>PropertiesChanged</c>/<c>ItemsPropertiesUpdated</c>
/// D-Bus signal (confirmed via <c>Avalonia.FreeDesktop.dll</c>'s <c>EmitdbusmenuPropertiesChanged</c>
/// and <c>QueueReset</c>), and the host panel (GNOME Shell's AppIndicator extension, KDE
/// Plasma's Task Manager) redraws that row on *receiving* the signal - independent of whether
/// the payload actually differs from what it already has. <see cref="UpdateLiveProgress"/>
/// therefore only assigns <c>Header</c> when the formatted text has actually changed since the
/// last tick (see <see cref="_lastHeaders"/>), and rounds the percentage to a whole number so
/// visually-meaningless 0.1%-of-a-second changes don't count as a change. This can't eliminate
/// flicker while a percentage is genuinely advancing - that's the host redrawing real new
/// text, not a bug - but it stops the constant every-400ms flicker on downloads that are
/// barely moving or between whole-percent boundaries.
/// </summary>
public sealed class TrayIconService
{
    /// <summary>How often <see cref="UpdateLiveProgress"/> refreshes percentage text. Coarser
    /// than <c>DownloadHandle</c>'s own 100ms progress-report throttle - a native menu doesn't
    /// need per-tick fidelity, just to not look frozen. Runs continuously rather than only while
    /// the popup is open (see the constructor's doc comment) - it's a cheap in-place property
    /// update over a typically-small row set, not worth the complexity of start/stop gating.</summary>
    private static readonly TimeSpan LiveUpdateInterval = TimeSpan.FromMilliseconds(400);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly DownloadListViewModel _downloads;
    private readonly SettingsViewModel _settings;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenu _menu = new();
    private readonly DispatcherTimer _liveUpdateTimer;

    /// <summary>Maps each currently-downloading row to the <see cref="NativeMenuItem"/>
    /// <see cref="RebuildMenu"/> created for it, so <see cref="UpdateLiveProgress"/> can push
    /// fresh percentage text into the existing item's <c>Header</c> instead of rebuilding
    /// <c>Items</c>. Repopulated on every <see cref="RebuildMenu"/> call; entries become stale
    /// (and are skipped) between a structural change and the next rebuild.</summary>
    private readonly Dictionary<Guid, NativeMenuItem> _downloadMenuItems = new();

    /// <summary>Last <see cref="FormatHeader"/> text actually assigned to each row's menu item,
    /// so <see cref="UpdateLiveProgress"/> can skip re-assigning <c>Header</c> (and the dbusmenu
    /// D-Bus signal that setting it triggers on Linux) when the formatted text hasn't changed -
    /// see this class's doc comment for why that matters. Repopulated alongside
    /// <see cref="_downloadMenuItems"/> on every <see cref="RebuildMenu"/> call.</summary>
    private readonly Dictionary<Guid, string> _lastHeaders = new();

    private bool _isExiting;

    public TrayIconService(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel mainWindowViewModel,
        TrayIcon trayIcon)
    {
        _desktop = desktop;
        _window = window;
        _downloads = mainWindowViewModel.DownloadListViewModel;
        _settings = mainWindowViewModel.SettingsViewModel;
        _trayIcon = trayIcon;

        _menu.NeedsUpdate += (_, _) => RebuildMenu();
        _downloads.DownloadsChanged += (_, _) => RebuildMenu();
        _trayIcon.Menu = _menu;
        _trayIcon.Clicked += (_, _) => ToggleWindow();

        // NativeMenu.Opening/Closed would be the obvious way to gate this timer to only-while-
        // the-popup-is-visible, and that was the first thing tried here - but on Linux those
        // events are backed by INativeMenuExporterEventsImplBridge.RaiseOpening()/RaiseClosed(),
        // and Avalonia.FreeDesktop.dll (the dbusmenu exporter GNOME's AppIndicator extension and
        // KDE's StatusNotifierItem both talk to) never references that interface anywhere -
        // confirmed by inspecting the assembly directly. So on this platform Opening/Closed
        // simply never fire, the timer never started, and Header stayed frozen at whatever the
        // last RebuildMenu() left it - which is exactly the bug this replaced. The same assembly
        // *does* watch NativeMenuItem property changes and emit a dbusmenu PropertiesChanged
        // D-Bus signal for them (EmitdbusmenuPropertiesChanged), so a Header update propagates
        // live to the host regardless of whether the popup happens to be open - there's no
        // upside to visibility-gating here even where Opening/Closed do work (Windows/macOS),
        // so this runs unconditionally for all platforms rather than branching per-OS.
        _liveUpdateTimer = new DispatcherTimer { Interval = LiveUpdateInterval };
        _liveUpdateTimer.Tick += (_, _) => UpdateLiveProgress();
        _liveUpdateTimer.Start();

        _window.Closing += OnWindowClosing;

        // Populate immediately rather than waiting for the first NeedsUpdate: some Linux
        // dbusmenu hosts query the menu's layout once up front and don't reliably re-request it
        // on every popup the way NeedsUpdate assumes, so a menu that starts empty can render as
        // "nothing happens on right-click" on first use. NeedsUpdate and DownloadsChanged both
        // keep it fresh thereafter.
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        _downloadMenuItems.Clear();
        _lastHeaders.Clear();

        _menu.Items.Add(new NativeMenuItem("Open AvaDM") { Command = new RelayCommand(RestoreWindow) });
        _menu.Items.Add(new NativeMenuItemSeparator());

        var downloadingRows = _downloads.GetDownloadingDownloads();
        if (downloadingRows.Count == 0)
        {
            _menu.Items.Add(new NativeMenuItem("No downloads in progress") { IsEnabled = false });
        }
        else
        {
            foreach (var row in downloadingRows)
            {
                var submenu = new NativeMenu();
                submenu.Items.Add(new NativeMenuItem("Pause") { Command = row.PauseCommand });
                submenu.Items.Add(new NativeMenuItem("Resume") { Command = row.ResumeCommand });

                var header = FormatHeader(row);
                var item = new NativeMenuItem(header) { Menu = submenu };
                _menu.Items.Add(item);
                _downloadMenuItems[row.Id] = item;
                _lastHeaders[row.Id] = header;
            }
        }

        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(new NativeMenuItem("Exit") { Command = new RelayCommand(RequestExit) });
    }

    /// <summary>Whole-number percent, not one decimal place: a coarser value changes less often,
    /// which directly reduces how frequently <see cref="UpdateLiveProgress"/> has anything new
    /// to push - see this class's doc comment on why that matters for flicker on Linux.</summary>
    private static string FormatHeader(DownloadRowViewModel row)
    {
        var displayedPercent = Math.Clamp(
            (int)Math.Round(row.ProgressPercent / 3.0) * 3,
            0,
            100);

        return $"{row.FileName} — {displayedPercent}% ({row.StatusText})";
    }
    
    /// <summary>Ticks continuously (see the constructor's doc comment on why this isn't gated
    /// to only-while-the-popup-is-open): refreshes each downloading row's existing menu item
    /// <c>Header</c> text in place, without touching <see cref="NativeMenu.Items"/> itself.
    /// Setting a <c>StyledProperty</c> like <c>Header</c> is a much lighter operation than the
    /// Items.Clear()-based <see cref="RebuildMenu"/>, which is what let the reverted 1-second
    /// poll timer visibly glitch the popup - this only touches text on items that already exist.
    /// A row with no entry in <see cref="_downloadMenuItems"/> means a structural change
    /// (<see cref="DownloadListViewModel.DownloadsChanged"/>) already rebuilt the menu out from
    /// under it since the last tick; that's harmless here, it's just skipped until the next
    /// rebuild repopulates the map.
    ///
    /// Only actually assigns <c>Header</c> (via <see cref="_lastHeaders"/>) when the formatted
    /// text differs from what's already there - a plain re-assignment of an unchanged value
    /// would still trigger the dbusmenu D-Bus signal that setting <c>Header</c> causes on
    /// Linux, and it's *that signal* the host panel redraws on, not anything Avalonia-side.
    /// See this class's doc comment for the full picture.</summary>
    private void UpdateLiveProgress()
    {
        foreach (var row in _downloads.GetDownloadingDownloads())
        {
            if (!_downloadMenuItems.TryGetValue(row.Id, out var item))
                continue;

            var header = FormatHeader(row);
            if (_lastHeaders.TryGetValue(row.Id, out var last) && last == header)
                continue;

            item.Header = header;
            _lastHeaders[row.Id] = header;
        }
    }

    private void RestoreWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>The tray icon's click handler: hides the window if it's currently shown
    /// (whether or not close-to-tray is enabled - this is a manual toggle, independent of that
    /// setting), or restores it if it's hidden or minimized.</summary>
    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            _window.Hide();
        }
        else
        {
            RestoreWindow();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting || !_settings.CloseToTray)
            return;

        e.Cancel = true;
        _window.Hide();
    }

    /// <summary>The tray menu's own Exit item - always performs a real shutdown, bypassing the
    /// close-to-tray setting entirely (that setting only governs the window's own Closing event).</summary>
    private void RequestExit()
    {
        _isExiting = true;
        _trayIcon.IsVisible = false;
        _desktop.Shutdown();
    }
}
