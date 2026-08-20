using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using AvaDM.Core;
using AvaDM.UI.Services;
using AvaDM.UI.ViewModels;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No DI container, per the plan - build the small object graph by hand and pass
            // instances through constructors, mirroring AvaDM.Console/Program.cs's wiring.
            var settings = new DownloadSettings();
            var uiPreferences = new UiPreferencesRepository(settings.GetResolvedRepositoryPath());

            var closeToTray = LoadStoredPreferences(uiPreferences);

            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var downloadManager = new DownloadManager(_httpClient, settings);

            var mainWindowViewModel = new MainWindowViewModel(downloadManager, settings, uiPreferences, closeToTray);
            var window = new MainWindow { DataContext = mainWindowViewModel };
            desktop.MainWindow = window;

            var trayIcon = TrayIcon.GetIcons(this)![0];
            _trayIconService = new TrayIconService(desktop, window, mainWindowViewModel, trayIcon);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Overrides App.axaml's static "Dark" default with whatever the user last chose in
    /// Settings > Appearance, read synchronously before the window is created so there's no flash
    /// of the wrong theme; also reads the close-to-tray preference the same way so
    /// <see cref="TrayIconService"/> has it from the first frame. Both reads share one best-effort
    /// try/catch, matching this method's original theme-only fallback style - a store that can't
    /// be read (e.g. permissions issue) shouldn't block startup, it should just fall back to
    /// defaults (static Dark, minimize-to-tray).</summary>
    private static bool LoadStoredPreferences(UiPreferencesRepository uiPreferences)
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
            return bool.TryParse(storedCloseToTray, out var closeToTray) ? closeToTray : true;
        }
        catch
        {
            // Best-effort: keep the static Dark theme default and fall back to minimize-to-tray
            // if the preferences store can't be read, rather than blocking startup on it.
            return true;
        }
    }
}
