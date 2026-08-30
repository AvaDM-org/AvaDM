using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaDM.Core;
using AvaDM.Core.Diagnostics;
using AvaDM.UI.Services;
using AvaDM.UI.ViewModels;
using Serilog;

namespace AvaDM.UI;

public partial class App : Application
{
    /// <summary>Shared for the process lifetime, exactly like AvaDM.Console/Program.cs's single
    /// <c>HttpClient</c> - one client backs every concurrent chunk task across every download.
    /// No explicit Dispose: this lives as long as the app process does, and disposing an
    /// `HttpClient` mid-shutdown buys nothing a desktop app needs.</summary>
    private HttpClient? _httpClient;

    /// <summary>Kept as a field purely for explicit lifetime rooting - its own event
    /// subscriptions on the long-lived <see cref="TrayIcon"/>/<see cref="Window"/> instances
    /// keep it alive for the process lifetime regardless, but an unrooted local would be an easy
    /// target for "why is this here" confusion later.</summary>
    private TrayIconService? _trayIconService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Registered before anything else runs, per Avalonia's documented strategy - covers
        // exceptions that escape the UI thread's message loop that AppDomain.UnhandledException
        // and TaskScheduler.UnobservedTaskException (installed in Program.Main) don't catch.
        // e.Handled is deliberately left false: this is a download manager mid-transfer, and
        // Avalonia's own docs warn that continuing after an unknown UI exception can leave the
        // app in an inconsistent state - safer to log, offer to report, and let it terminate.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled UI thread exception");
            Log.CloseAndFlush();
            CrashReporter.Report(e.Exception);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No DI container, per the plan - build the small object graph by hand and pass
            // instances through constructors, mirroring AvaDM.Console/Program.cs's wiring.
            var settings = new DownloadSettings();
            var uiPreferences = new UiPreferencesRepository(settings.GetResolvedRepositoryPath());

            var (closeToTray, doubleClickAction, autoUpdateEnabled) = LoadStoredPreferences(uiPreferences);

            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var downloadManager = new DownloadManager(_httpClient, settings);
            var updateService = new UpdateService(_httpClient);

            var mainWindowViewModel = new MainWindowViewModel(
                downloadManager, settings, uiPreferences, closeToTray, doubleClickAction,
                autoUpdateEnabled, updateService, () => desktop.Shutdown());
            var window = new MainWindow { DataContext = mainWindowViewModel };
            desktop.MainWindow = window;

            // Set by AutoStartService's registered autostart command so a login-triggered launch
            // starts hidden in the tray instead of popping the main window. Avalonia's classic
            // desktop lifetime shows desktop.MainWindow unconditionally once this method returns,
            // so hiding it in the Opened handler (rather than skipping the MainWindow assignment
            // above) is what avoids that - it costs a brief window flash instead of a more
            // invasive change to how MainWindow/TrayIconService are wired up.
            //
            // ShowInTaskbar = false right after Hide() works around a real Avalonia rendering bug
            // on Linux (and, per the upstream reports, macOS) where a window hidden this way comes
            // back from the next Show() with no content ever drawn - visible on the desktop only as
            // an inert, blank taskbar/dock entry the user has to close by hand, since there's
            // nothing on screen to interact with. Windows already hides the taskbar entry itself
            // when the window is hidden, so this is a no-op there. See
            // https://github.com/AvaloniaUI/Avalonia/issues/2994 and
            // https://github.com/AvaloniaUI/Avalonia/issues/18148 - TrayIconService.RestoreWindow
            // sets ShowInTaskbar back to true before the matching Show().
            if (desktop.Args?.Contains("--minimized") == true)
            {
                window.Opened += (_, _) =>
                {
                    window.Hide();
                    window.ShowInTaskbar = false;
                };
            }

            var trayIcon = TrayIcon.GetIcons(this)![0];
            _trayIconService = new TrayIconService(desktop, window, mainWindowViewModel, trayIcon);

            // Instance is null here only if SingleInstanceService.TryAcquire() itself is
            // somehow bypassed (e.g. a future test/design-time path that doesn't go through
            // Program.Main) - Program.Main already exits before reaching this method otherwise.
            // The service has been listening since before this method even started, so this call
            // also replays any activation request that arrived during startup (see
            // SingleInstanceService.SetActivationHandler).
            SingleInstanceService.Instance?.SetActivationHandler(() => Dispatcher.UIThread.Post(() =>
            {
                _trayIconService.RestoreWindow();
                mainWindowViewModel.DownloadListViewModel.ShowToast("AvaDM is already running.");
            }));

            // A login-autostart entry or applications-menu shortcut written by an earlier run can
            // point at an executable that isn't there any more - an AppImage's /tmp mount path is
            // gone the moment the process that wrote it exits, and a portable build can simply be
            // moved. Repairing them here means the next login works, instead of the desktop
            // reporting "Could not find the program ..." and AvaDM silently not starting.
            AutoStartService.RefreshIfStale();
            DesktopShortcutService.RefreshIfStale();

            // Fire-and-forget: CheckForUpdatesAsync swallows its own failures (silent: true means
            // "don't surface an error/"you're up to date" message", not "don't check"), so there's
            // nothing here to await or observe. An available update still updates
            // SettingsViewModel's state, which both the Settings page and the tray menu pick up.
            if (mainWindowViewModel.SettingsViewModel.AutoUpdateEnabled)
            {
                _ = mainWindowViewModel.SettingsViewModel.CheckForUpdatesAsync(silent: true);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Overrides App.axaml's static "Dark" default with whatever the user last chose in
    /// Settings > Appearance, read synchronously before the window is created so there's no flash
    /// of the wrong theme; also reads the close-to-tray and downloaded-item double-click
    /// preferences the same way so <see cref="TrayIconService"/> and the downloads list have them
    /// from the first frame. All reads share one best-effort try/catch, matching this method's
    /// original theme-only fallback style - a store that can't be read (e.g. permissions issue)
    /// shouldn't block startup, it should just fall back to defaults (static Dark, minimize-to-
    /// tray, double-click opens the file).</summary>
    private static (bool CloseToTray, DownloadDoubleClickAction DoubleClickAction, bool AutoUpdateEnabled) LoadStoredPreferences(
        UiPreferencesRepository uiPreferences)
    {
        try
        {
            uiPreferences.InitializeAsync().GetAwaiter().GetResult();

            var storedTheme = uiPreferences.GetValueAsync(UiPreferencesRepository.ThemeVariantKey).GetAwaiter().GetResult();
            if (storedTheme is not null)
            {
                Current!.RequestedThemeVariant = storedTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            }

            var storedCloseToTray = uiPreferences.GetValueAsync(UiPreferencesRepository.CloseToTrayKey).GetAwaiter().GetResult();
            var closeToTray = bool.TryParse(storedCloseToTray, out var parsedCloseToTray) ? parsedCloseToTray : true;

            var storedDoubleClickAction = uiPreferences.GetValueAsync(UiPreferencesRepository.DoubleClickActionKey).GetAwaiter().GetResult();
            var doubleClickAction = storedDoubleClickAction == "OpenContainingFolder"
                ? DownloadDoubleClickAction.OpenContainingFolder
                : DownloadDoubleClickAction.OpenFile;

            var storedAutoUpdateEnabled = uiPreferences.GetValueAsync(UiPreferencesRepository.AutoUpdateEnabledKey).GetAwaiter().GetResult();
            var autoUpdateEnabled = bool.TryParse(storedAutoUpdateEnabled, out var parsedAutoUpdateEnabled) ? parsedAutoUpdateEnabled : true;

            return (closeToTray, doubleClickAction, autoUpdateEnabled);
        }
        catch
        {
            // Best-effort: keep the static Dark theme default and fall back to minimize-to-tray,
            // open-file-on-double-click, and auto-update-on if the preferences store can't be
            // read, rather than blocking startup on it.
            return (true, DownloadDoubleClickAction.OpenFile, true);
        }
    }
}
