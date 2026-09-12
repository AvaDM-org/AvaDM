using System.Collections.ObjectModel;
using AvaDM.Core;
using AvaDM.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// Shell view model: owns which page is on screen and is the composition root for the two page
/// view models (constructed here, not passed in, so each can be handed a navigate-back callback
/// that closes over this instance's own command methods). Per design.md's Layout section this is
/// a single window with no persistent app-chrome nav bar - the Downloads page's own toolbar
/// carries a "Settings" entry point, and the Settings page carries its own way back, both wired
/// through the callbacks below. MainWindow.axaml just swaps <see cref="CurrentPageViewModel"/>
/// through a ContentControl + DataTemplates - no navigation framework, per the plan.
///
/// Toasts also live here rather than on <see cref="DownloadListViewModel"/>: an update-available
/// toast (see <see cref="ShowUpdateAvailableToast"/>) can fire from a startup check while either
/// page is on screen, and MainWindow.axaml renders the overlay above whichever page is current.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly DownloadListViewModel _downloadListViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty]
    private ViewModelBase _currentPageViewModel;

    /// <summary>Transient toast/snackbar notifications - see <see cref="ToastViewModel"/>.</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    public MainWindowViewModel(
        DownloadManager downloadManager,
        DownloadSettings settings,
        UiPreferencesRepository uiPreferences,
        bool closeToTray,
        DownloadDoubleClickAction doubleClickAction,
        bool autoUpdateEnabled,
        UpdateService updateService,
        Action requestAppExit)
    {
        _settingsViewModel = new SettingsViewModel(
            settings, uiPreferences, NavigateToDownloads, closeToTray, doubleClickAction,
            autoUpdateEnabled, updateService, ShowUpdateAvailableToast, requestAppExit);
        _downloadListViewModel = new DownloadListViewModel(
            downloadManager, settings, uiPreferences, NavigateToSettings, () => _settingsViewModel.DoubleClickAction, ShowToast);
        _currentPageViewModel = _downloadListViewModel;
    }

    /// <summary>Exposed so <see cref="Services.TrayIconService"/> can reach the live downloads
    /// list (for the tray menu's per-download entries) without new plumbing.</summary>
    public DownloadListViewModel DownloadListViewModel => _downloadListViewModel;

    /// <summary>Exposed so <see cref="Services.TrayIconService"/> can read the current
    /// close-to-tray setting at window-close time.</summary>
    public SettingsViewModel SettingsViewModel => _settingsViewModel;

    [RelayCommand]
    private void NavigateToDownloads() => CurrentPageViewModel = _downloadListViewModel;

    [RelayCommand]
    private void NavigateToSettings() => CurrentPageViewModel = _settingsViewModel;

    /// <summary>Public so callers outside this view model - currently just
    /// <see cref="App.OnFrameworkInitializationCompleted"/>, notifying that a second launch was
    /// redirected here - can post a toast without duplicating <see cref="ToastViewModel"/>'s
    /// wiring.</summary>
    public void ShowToast(string message) => Toasts.Add(new ToastViewModel(message, RemoveToast));

    /// <summary>Passed into <see cref="SettingsViewModel"/> as the callback for a silent startup
    /// update check that finds a new version - see that class's <c>CheckForUpdatesAsync</c>.
    /// Doesn't auto-dismiss (the user may not be looking at the moment it appears) and its
    /// "Update now" action jumps to the Settings page, where the actual download/pause/resume/
    /// cancel progress is shown, then starts the install exactly as clicking Settings' own
    /// Install Update button would.</summary>
    private void ShowUpdateAvailableToast(UpdateCheckResult update) =>
        Toasts.Add(new ToastViewModel(
            $"AvaDM {update.LatestVersion} is available.",
            RemoveToast,
            actionLabel: "Update now",
            onAction: () =>
            {
                NavigateToSettings();
                _settingsViewModel.InstallUpdateCommand.Execute(null);
            },
            autoDismiss: false));

    private void RemoveToast(ToastViewModel toast)
    {
        toast.Dispose();
        Toasts.Remove(toast);
    }
}
