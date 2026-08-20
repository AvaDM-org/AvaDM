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
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly DownloadListViewModel _downloadListViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty]
    private ViewModelBase _currentPageViewModel;

    public MainWindowViewModel(
        DownloadManager downloadManager,
        DownloadSettings settings,
        UiPreferencesRepository uiPreferences,
        bool closeToTray,
        DownloadDoubleClickAction doubleClickAction)
    {
        _settingsViewModel = new SettingsViewModel(settings, uiPreferences, NavigateToDownloads, closeToTray, doubleClickAction);
        _downloadListViewModel = new DownloadListViewModel(
            downloadManager, NavigateToSettings, () => _settingsViewModel.DoubleClickAction);
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
}
