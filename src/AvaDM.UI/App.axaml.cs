using Avalonia;
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

            ApplyStoredThemePreference(uiPreferences);

            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var downloadManager = new DownloadManager(_httpClient, settings);

            var mainWindowViewModel = new MainWindowViewModel(downloadManager, settings, uiPreferences);
            desktop.MainWindow = new MainWindow { DataContext = mainWindowViewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Overrides App.axaml's static "Dark" default with whatever the user last chose in
    /// Settings > Appearance, read synchronously before the window is created so there's no flash
    /// of the wrong theme.</summary>
    private static void ApplyStoredThemePreference(UiPreferencesRepository uiPreferences)
    {
        try
        {
            uiPreferences.InitializeAsync().GetAwaiter().GetResult();
            var stored = uiPreferences.GetValueAsync(UiPreferencesRepository.ThemeVariantKey).GetAwaiter().GetResult();

            if (stored is not null)
            {
                Current!.RequestedThemeVariant = stored == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            }
        }
        catch
        {
            // Best-effort: keep App.axaml's static Dark default if the preferences store can't
            // be read (e.g. permissions issue) rather than blocking startup on it.
        }
    }
}
