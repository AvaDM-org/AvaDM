using Avalonia;
using Avalonia.Styling;
using AvaDM.Core;
using AvaDM.Core.Diagnostics;
using AvaDM.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace AvaDM.UI.ViewModels;

/// <summary>
/// Settings page with staged edits over the shared <see cref="DownloadSettings"/> instance and
/// an Appearance theme toggle that is persisted immediately through
/// <see cref="UiPreferencesRepository"/>.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly DownloadSettings _settings;
    private readonly UiPreferencesRepository _uiPreferences;
    private readonly Action _navigateToDownloads;
    private readonly UpdateService _updateService;
    private readonly Action _requestAppExit;

    [ObservableProperty]
    private string _downloadDirectory;

    [ObservableProperty]
    private int _chunkCount;

    [ObservableProperty]
    private long? _speedLimitBytesPerSecond;

    [ObservableProperty]
    private string? _repositoryPathInput;

    [ObservableProperty]
    private int _maxRetryAttempts;

    [ObservableProperty]
    private string _retryBaseDelaySecondsInput;

    [ObservableProperty]
    private string _perAttemptTimeoutSecondsInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDarkSelected))]
    [NotifyPropertyChangedFor(nameof(IsLightSelected))]
    private bool _isDarkTheme;

    /// <summary>Whether closing the main window minimizes to tray (true) instead of exiting the
    /// app (false). Persisted immediately on change, same as the theme toggle above; also read
    /// by <see cref="Services.TrayIconService"/> at window-close time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloseToTraySelected))]
    [NotifyPropertyChangedFor(nameof(IsCloseAppSelected))]
    private bool _closeToTray;

    /// <summary>What double-clicking a completed row in the downloads list does. Persisted
    /// immediately on change, same as the theme and close-to-tray toggles above; read live by
    /// each <see cref="DownloadRowViewModel"/> via a getter closure - see
    /// <see cref="MainWindowViewModel"/>'s wiring - so a change here takes effect on the next
    /// double-click without needing to reopen the app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoubleClickOpenFileSelected))]
    [NotifyPropertyChangedFor(nameof(IsDoubleClickOpenContainingFolderSelected))]
    private DownloadDoubleClickAction _doubleClickAction;

    /// <summary>Whether AvaDM launches automatically at login. Unlike the toggles above, this
    /// isn't mirrored through <see cref="UiPreferencesRepository"/> - <see cref="AutoStartService"/>
    /// reads/writes the OS's own autostart entry directly, since that entry (not our preferences
    /// store) is what the OS actually acts on, and it can be toggled outside the app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartWithSystemEnabledSelected))]
    [NotifyPropertyChangedFor(nameof(IsStartWithSystemDisabledSelected))]
    private bool _startWithSystem;

    /// <summary>Whether AvaDM has an applications-menu entry, via <see cref="DesktopShortcutService"/>.
    /// Only meaningful (and only shown in the view) on Linux - see that class for why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesktopShortcutCreatedSelected))]
    [NotifyPropertyChangedFor(nameof(IsDesktopShortcutRemovedSelected))]
    private bool _hasDesktopShortcut;

    /// <summary>Whether AvaDM checks for updates on startup. Persisted through
    /// <see cref="UiPreferencesRepository"/>, same as the theme/close-to-tray/double-click
    /// toggles - unlike <see cref="StartWithSystem"/> and <see cref="HasDesktopShortcut"/> above,
    /// there's no external OS entry to treat as the source of truth here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoUpdateEnabledSelected))]
    [NotifyPropertyChangedFor(nameof(IsAutoUpdateDisabledSelected))]
    private bool _autoUpdateEnabled;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatusMessage))]
    private string? _updateStatusMessage;

    /// <summary>Null until the first check completes. <see cref="IsUpdateAvailable"/> and
    /// <see cref="AvailableUpdateVersion"/> are derived from this rather than being separate
    /// observable fields, so there's one source of truth for "is there an update" that both the
    /// Settings view and <see cref="Services.TrayIconService"/> (via
    /// <see cref="UpdateAvailabilityChanged"/>) agree on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateVersion))]
    private UpdateCheckResult? _latestUpdateCheck;

    /// <summary>Raised whenever a check completes, so <see cref="Services.TrayIconService"/> can
    /// rebuild its menu to show/hide the "Update available" entry without polling.</summary>
    public event EventHandler? UpdateAvailabilityChanged;

    public SettingsViewModel(
        DownloadSettings settings,
        UiPreferencesRepository uiPreferences,
        Action navigateToDownloads,
        bool closeToTray,
        DownloadDoubleClickAction doubleClickAction,
        bool autoUpdateEnabled,
        UpdateService updateService,
        Action requestAppExit)
    {
        _settings = settings;
        _uiPreferences = uiPreferences;
        _navigateToDownloads = navigateToDownloads;
        _updateService = updateService;
        _requestAppExit = requestAppExit;

        _downloadDirectory = settings.DefaultDownloadDirectory;
        _chunkCount = settings.DefaultChunkCount;
        _speedLimitBytesPerSecond = settings.DefaultSpeedLimitBytesPerSecond;
        _repositoryPathInput = settings.RepositoryPath ?? string.Empty;
        _maxRetryAttempts = settings.DefaultMaxRetryAttempts;
        _retryBaseDelaySecondsInput = settings.DefaultRetryBaseDelay.TotalSeconds.ToString("0.##");
        _perAttemptTimeoutSecondsInput = settings.DefaultPerAttemptTimeout.TotalSeconds.ToString("0.##");
        _isDarkTheme = Application.Current!.RequestedThemeVariant == ThemeVariant.Dark;
        _closeToTray = closeToTray;
        _doubleClickAction = doubleClickAction;
        _startWithSystem = AutoStartService.IsEnabled();
        _hasDesktopShortcut = DesktopShortcutService.IsCreated();
        _autoUpdateEnabled = autoUpdateEnabled;
    }

    public bool ShowDesktopShortcutSection => OperatingSystem.IsLinux();

    /// <summary>The version of the build that's actually running - shown in Settings so the user
    /// can confirm which version they're on, particularly after a self-update (nothing else about
    /// the install changes visibly, and an AppImage/portable file keeps whatever name it was
    /// downloaded under).</summary>
    public string CurrentVersion => UpdateService.CurrentVersionDisplay;

    public bool IsUpdateAvailable => LatestUpdateCheck?.IsAvailable == true;

    public string? AvailableUpdateVersion => LatestUpdateCheck?.LatestVersion;

    partial void OnLatestUpdateCheckChanged(UpdateCheckResult? value)
    {
        UpdateAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    public string ResolvedRepositoryPathHint => _settings.GetResolvedRepositoryPath();

    public string LogDirectoryHint => AppLogging.LogDirectory;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool IsDarkSelected => IsDarkTheme;

    public bool IsLightSelected => !IsDarkTheme;

    public bool IsCloseToTraySelected => CloseToTray;

    public bool IsCloseAppSelected => !CloseToTray;

    public bool IsStartWithSystemEnabledSelected => StartWithSystem;

    public bool IsStartWithSystemDisabledSelected => !StartWithSystem;

    public bool IsDesktopShortcutCreatedSelected => HasDesktopShortcut;

    public bool IsDesktopShortcutRemovedSelected => !HasDesktopShortcut;

    public bool IsAutoUpdateEnabledSelected => AutoUpdateEnabled;

    public bool IsAutoUpdateDisabledSelected => !AutoUpdateEnabled;

    public bool HasUpdateStatusMessage => !string.IsNullOrEmpty(UpdateStatusMessage);

    public bool IsDoubleClickOpenFileSelected => DoubleClickAction == DownloadDoubleClickAction.OpenFile;

    public bool IsDoubleClickOpenContainingFolderSelected => DoubleClickAction == DownloadDoubleClickAction.OpenContainingFolder;

    [RelayCommand]
    private void CloseSettings() => _navigateToDownloads();

    [RelayCommand]
    private void OpenLogFolder() => CrashReporter.OpenLogFolder();

    [RelayCommand]
    private async Task SelectCloseToTray()
    {
        CloseToTray = true;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.CloseToTrayKey, "true");
    }

    [RelayCommand]
    private async Task SelectCloseApp()
    {
        CloseToTray = false;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.CloseToTrayKey, "false");
    }

    [RelayCommand]
    private void SelectStartWithSystemEnabled()
    {
        ErrorMessage = null;
        if (AutoStartService.SetEnabled(true))
        {
            StartWithSystem = true;
        }
        else
        {
            ErrorMessage = "Couldn't enable starting AvaDM at login.";
        }
    }

    [RelayCommand]
    private void SelectStartWithSystemDisabled()
    {
        ErrorMessage = null;
        if (AutoStartService.SetEnabled(false))
        {
            StartWithSystem = false;
        }
        else
        {
            ErrorMessage = "Couldn't disable starting AvaDM at login.";
        }
    }

    [RelayCommand]
    private void CreateDesktopShortcut()
    {
        ErrorMessage = null;
        if (DesktopShortcutService.SetCreated(true))
        {
            HasDesktopShortcut = true;
        }
        else
        {
            ErrorMessage = "Couldn't create the desktop shortcut.";
        }
    }

    [RelayCommand]
    private void RemoveDesktopShortcut()
    {
        ErrorMessage = null;
        if (DesktopShortcutService.SetCreated(false))
        {
            HasDesktopShortcut = false;
        }
        else
        {
            ErrorMessage = "Couldn't remove the desktop shortcut.";
        }
    }

    [RelayCommand]
    private async Task SelectAutoUpdateEnabled()
    {
        AutoUpdateEnabled = true;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.AutoUpdateEnabledKey, "true");
    }

    [RelayCommand]
    private async Task SelectAutoUpdateDisabled()
    {
        AutoUpdateEnabled = false;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.AutoUpdateEnabledKey, "false");
    }

    /// <summary>Runs a check without user-facing chrome around it - used for the silent
    /// startup check (see App.axaml.cs) as well as by <see cref="CheckForUpdates"/> below.
    /// <paramref name="silent"/> only affects <see cref="UpdateStatusMessage"/> noise: an
    /// available update is always surfaced (that's the whole point), but "you're up to date" and
    /// error text are only shown for an explicit, user-initiated check.</summary>
    public async Task CheckForUpdatesAsync(bool silent)
    {
        if (IsCheckingForUpdates)
            return;

        IsCheckingForUpdates = true;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
        if (!silent)
            UpdateStatusMessage = "Checking for updates...";

        try
        {
            var result = await _updateService.CheckForUpdateAsync();
            LatestUpdateCheck = result;
            UpdateStatusMessage = result.IsAvailable
                ? $"AvaDM {result.LatestVersion} is available."
                : silent ? null : "You're up to date.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            if (!silent)
                UpdateStatusMessage = $"Couldn't check for updates: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
            CheckForUpdatesCommand.NotifyCanExecuteChanged();
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdates() => CheckForUpdatesAsync(silent: false);

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (LatestUpdateCheck is not { IsAvailable: true } update)
            return;

        IsCheckingForUpdates = true;
        InstallUpdateCommand.NotifyCanExecuteChanged();
        UpdateStatusMessage = "Installing update...";

        try
        {
            var progress = new Progress<string>(message => UpdateStatusMessage = message);
            var result = await _updateService.ApplyUpdateAsync(update, progress);
            UpdateStatusMessage = result.Message ?? (result.Succeeded ? "Update applied." : "Update failed.");

            if (result.Succeeded && result.ShouldExitApp)
                _requestAppExit();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update install failed");
            UpdateStatusMessage = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstallUpdate() => IsUpdateAvailable && !IsCheckingForUpdates;

    [RelayCommand]
    private async Task SelectDoubleClickOpenFile()
    {
        DoubleClickAction = DownloadDoubleClickAction.OpenFile;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.DoubleClickActionKey, "OpenFile");
    }

    [RelayCommand]
    private async Task SelectDoubleClickOpenContainingFolder()
    {
        DoubleClickAction = DownloadDoubleClickAction.OpenContainingFolder;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.DoubleClickActionKey, "OpenContainingFolder");
    }

    [RelayCommand]
    private async Task SelectDarkTheme()
    {
        IsDarkTheme = true;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.ThemeVariantKey, "Dark");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }

    [RelayCommand]
    private async Task SelectLightTheme()
    {
        IsDarkTheme = false;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.ThemeVariantKey, "Light");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        StatusMessage = null;

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            ErrorMessage = "Download directory is required.";
            return;
        }

        if (!double.TryParse(RetryBaseDelaySecondsInput, out var retryBaseDelaySeconds)
            || retryBaseDelaySeconds <= 0)
        {
            ErrorMessage = "Retry base delay must be a positive number of seconds.";
            return;
        }

        if (!double.TryParse(PerAttemptTimeoutSecondsInput, out var perAttemptTimeoutSeconds)
            || perAttemptTimeoutSeconds <= 0)
        {
            ErrorMessage = "Per-attempt timeout must be a positive number of seconds.";
            return;
        }

        _settings.DefaultDownloadDirectory = DownloadDirectory.Trim();
        _settings.DefaultChunkCount = ChunkCount;
        _settings.DefaultSpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond;
        _settings.RepositoryPath = string.IsNullOrWhiteSpace(RepositoryPathInput)
            ? null
            : RepositoryPathInput.Trim();
        _settings.DefaultMaxRetryAttempts = MaxRetryAttempts;
        _settings.DefaultRetryBaseDelay = TimeSpan.FromSeconds(retryBaseDelaySeconds);
        _settings.DefaultPerAttemptTimeout = TimeSpan.FromSeconds(perAttemptTimeoutSeconds);
        StatusMessage = "Settings saved.";
    }
}
