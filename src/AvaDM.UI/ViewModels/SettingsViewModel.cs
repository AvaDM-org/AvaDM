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
    private readonly DownloadManager _downloadManager;
    private readonly UiPreferencesRepository _uiPreferences;
    private readonly Action _navigateToDownloads;
    private readonly UpdateService _updateService;
    private readonly Action<UpdateCheckResult> _onUpdateAvailable;
    private readonly Action _requestAppExit;

    private CancellationTokenSource? _updateCts;
    private PauseTokenSource? _updatePauseTokenSource;

    /// <summary>Factory defaults, used only to reset a numeric field that's been cleared to blank
    /// (see the On*Changed handlers below) - a fresh instance rather than duplicated literals, so
    /// they can't drift from <see cref="DownloadSettings"/>'s own initializers.</summary>
    private static readonly DownloadSettings s_factoryDefaults = new();

    [ObservableProperty]
    private string _downloadDirectory;

    [ObservableProperty]
    private int? _chunkCount;

    [ObservableProperty]
    private long? _speedLimitBytesPerSecond;

    [ObservableProperty]
    private string? _repositoryPathInput;

    [ObservableProperty]
    private int? _maxRetryAttempts;

    [ObservableProperty]
    private string _retryBaseDelaySecondsInput;

    [ObservableProperty]
    private string _inactivityTimeoutSecondsInput;

    [ObservableProperty]
    private int? _autoRetryAttempts;

    [ObservableProperty]
    private int? _maxConcurrentDownloads;

    /// <summary>Whether DownloadManager automatically resumes everything left "Interrupted" (see
    /// <see cref="AvaDM.Core.DownloadSettings.AutoResumeDownloadsOnStartup"/>) the next time the
    /// app starts. Persisted immediately through <see cref="UiPreferencesRepository"/>, like the
    /// theme/close-to-tray/auto-update toggles above, rather than staged until Save() - unlike
    /// those, a plain DownloadSettings field alone can't make this setting do anything at all
    /// across a restart (DownloadSettings itself resets to code defaults on every launch), so
    /// App.axaml.cs reads the persisted value back and applies it before DownloadManager first
    /// initializes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutoResumeDownloadsOnStartupEnabledSelected))]
    [NotifyPropertyChangedFor(nameof(IsAutoResumeDownloadsOnStartupDisabledSelected))]
    private bool _autoResumeDownloadsOnStartup;

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

    /// <summary>True for the whole span of <see cref="InstallUpdate"/> - drives the update
    /// progress bar and its pause/resume/cancel row in the view. Unlike <see cref="IsCheckingForUpdates"/>
    /// (which also covers the initial "Check for Updates" click), this is specifically the
    /// download-and-apply phase.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelUpdate))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    private bool _isUpdateDownloading;

    /// <summary>True only while the byte-level asset download is in flight - as opposed to the
    /// verify/install/restart phases that follow it, which can't meaningfully be paused. Toggled
    /// off the coarse phase text from <see cref="UpdateService.ApplyUpdateAsync"/>'s
    /// <c>IProgress&lt;string&gt;</c> rather than a separate state machine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseUpdate))]
    [NotifyPropertyChangedFor(nameof(CanResumeUpdate))]
    [NotifyCanExecuteChangedFor(nameof(PauseUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdateCommand))]
    private bool _isUpdateDownloadActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPauseUpdate))]
    [NotifyPropertyChangedFor(nameof(CanResumeUpdate))]
    [NotifyCanExecuteChangedFor(nameof(PauseUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdateCommand))]
    private bool _isUpdatePaused;

    [ObservableProperty]
    private double _updateDownloadProgressPercent;

    [ObservableProperty]
    private bool _updateDownloadIsIndeterminate;

    public bool CanPauseUpdate => IsUpdateDownloadActive && !IsUpdatePaused;

    public bool CanResumeUpdate => IsUpdateDownloadActive && IsUpdatePaused;

    public bool CanCancelUpdate => IsUpdateDownloading;

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
        DownloadManager downloadManager,
        UiPreferencesRepository uiPreferences,
        Action navigateToDownloads,
        bool closeToTray,
        DownloadDoubleClickAction doubleClickAction,
        bool autoUpdateEnabled,
        bool autoResumeDownloadsOnStartup,
        UpdateService updateService,
        Action<UpdateCheckResult> onUpdateAvailable,
        Action requestAppExit)
    {
        _settings = settings;
        _downloadManager = downloadManager;
        _uiPreferences = uiPreferences;
        _navigateToDownloads = navigateToDownloads;
        _updateService = updateService;
        _onUpdateAvailable = onUpdateAvailable;
        _requestAppExit = requestAppExit;

        _downloadDirectory = settings.DefaultDownloadDirectory;
        _chunkCount = settings.DefaultChunkCount;
        _speedLimitBytesPerSecond = settings.DefaultSpeedLimitBytesPerSecond;
        _repositoryPathInput = settings.RepositoryPath ?? string.Empty;
        _maxRetryAttempts = settings.DefaultMaxRetryAttempts;
        _retryBaseDelaySecondsInput = settings.DefaultRetryBaseDelay.TotalSeconds.ToString("0.##");
        _inactivityTimeoutSecondsInput = settings.DefaultInactivityTimeout.TotalSeconds.ToString("0.##");
        _autoRetryAttempts = settings.DefaultAutoRetryAttempts;
        _maxConcurrentDownloads = settings.DefaultMaxConcurrentDownloads;
        _autoResumeDownloadsOnStartup = autoResumeDownloadsOnStartup;
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

    // NumericUpDown.Value is nullable (it goes null while the box is blank, e.g. the user
    // selected-all-and-deleted mid-edit) - these four settings used to be non-nullable int, so
    // that null failed to bind and Avalonia rendered the raw InvalidCastException inline. Rather
    // than surface an error for a still-in-progress edit, just snap back to the factory default.
    partial void OnChunkCountChanged(int? value)
    {
        if (value is null)
            ChunkCount = s_factoryDefaults.DefaultChunkCount;
    }

    partial void OnMaxRetryAttemptsChanged(int? value)
    {
        if (value is null)
            MaxRetryAttempts = s_factoryDefaults.DefaultMaxRetryAttempts;
    }

    partial void OnAutoRetryAttemptsChanged(int? value)
    {
        if (value is null)
            AutoRetryAttempts = s_factoryDefaults.DefaultAutoRetryAttempts;
    }

    partial void OnMaxConcurrentDownloadsChanged(int? value)
    {
        if (value is null)
            MaxConcurrentDownloads = s_factoryDefaults.DefaultMaxConcurrentDownloads;
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

    public bool IsAutoResumeDownloadsOnStartupEnabledSelected => AutoResumeDownloadsOnStartup;

    public bool IsAutoResumeDownloadsOnStartupDisabledSelected => !AutoResumeDownloadsOnStartup;

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

    [RelayCommand]
    private async Task SelectAutoResumeDownloadsOnStartupEnabled()
    {
        AutoResumeDownloadsOnStartup = true;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.AutoResumeDownloadsOnStartupKey, "true");
    }

    [RelayCommand]
    private async Task SelectAutoResumeDownloadsOnStartupDisabled()
    {
        AutoResumeDownloadsOnStartup = false;
        await _uiPreferences.SetValueAsync(UiPreferencesRepository.AutoResumeDownloadsOnStartupKey, "false");
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

            // Only the silent (startup) check notifies via toast - a manual, user-initiated check
            // already has the user looking at this page, where the same information is already
            // shown inline above.
            if (silent && result.IsAvailable)
                _onUpdateAvailable(result);
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
        IsUpdateDownloading = true;
        UpdateDownloadProgressPercent = 0;
        UpdateDownloadIsIndeterminate = true;
        InstallUpdateCommand.NotifyCanExecuteChanged();
        UpdateStatusMessage = "Installing update...";

        _updateCts = new CancellationTokenSource();
        _updatePauseTokenSource = new PauseTokenSource();

        try
        {
            var progress = new Progress<string>(message =>
            {
                UpdateStatusMessage = message;
                IsUpdateDownloadActive = string.Equals(message, "Downloading update...", StringComparison.Ordinal);
            });
            var downloadProgress = new Progress<UpdateDownloadProgress>(p =>
            {
                UpdateDownloadIsIndeterminate = p.PercentComplete is null;
                UpdateDownloadProgressPercent = p.PercentComplete ?? 0;
            });

            var result = await _updateService.ApplyUpdateAsync(
                update, progress, downloadProgress, _updatePauseTokenSource, _updateCts.Token);
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
            IsUpdateDownloading = false;
            IsUpdateDownloadActive = false;
            IsUpdatePaused = false;
            _updateCts?.Dispose();
            _updateCts = null;
            _updatePauseTokenSource = null;
            InstallUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstallUpdate() => IsUpdateAvailable && !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanPauseUpdate))]
    private void PauseUpdate()
    {
        _updatePauseTokenSource?.Pause();
        IsUpdatePaused = true;
    }

    [RelayCommand(CanExecute = nameof(CanResumeUpdate))]
    private void ResumeUpdate()
    {
        _updatePauseTokenSource?.Resume();
        IsUpdatePaused = false;
    }

    /// <summary>No confirmation, unlike a download row's Cancel - the update asset is a throwaway
    /// staging file the user never asked to keep, so it's just deleted (see
    /// <see cref="UpdateService"/>'s cleanup on a cancelled/failed apply) rather than prompted
    /// over.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelUpdate))]
    private void CancelUpdate() => _updateCts?.Cancel();

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
    private async Task Save()
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

        if (!double.TryParse(InactivityTimeoutSecondsInput, out var inactivityTimeoutSeconds)
            || inactivityTimeoutSeconds <= 0)
        {
            ErrorMessage = "Inactivity timeout must be a positive number of seconds.";
            return;
        }

        if (MaxConcurrentDownloads <= 0)
        {
            ErrorMessage = "Max concurrent downloads must be at least 1.";
            return;
        }

        _settings.DefaultDownloadDirectory = DownloadDirectory.Trim();
        _settings.DefaultChunkCount = ChunkCount ?? s_factoryDefaults.DefaultChunkCount;
        _settings.DefaultSpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond;
        _settings.RepositoryPath = string.IsNullOrWhiteSpace(RepositoryPathInput)
            ? null
            : RepositoryPathInput.Trim();
        _settings.DefaultMaxRetryAttempts = MaxRetryAttempts ?? s_factoryDefaults.DefaultMaxRetryAttempts;
        _settings.DefaultRetryBaseDelay = TimeSpan.FromSeconds(retryBaseDelaySeconds);
        _settings.DefaultInactivityTimeout = TimeSpan.FromSeconds(inactivityTimeoutSeconds);
        _settings.DefaultAutoRetryAttempts = AutoRetryAttempts ?? s_factoryDefaults.DefaultAutoRetryAttempts;
        _settings.DefaultMaxConcurrentDownloads = MaxConcurrentDownloads ?? s_factoryDefaults.DefaultMaxConcurrentDownloads;

        // A lowered limit should re-queue however many currently-running downloads are needed to
        // get back under it - so they pick back up on their own once room exists again, rather
        // than sitting paused until manually resumed or left running until they happen to finish
        // on their own. A no-op if the limit was raised or unchanged, or if nothing is over it.
        await _downloadManager.EnforceConcurrencyLimitAsync();

        // A raised limit should let already-queued downloads start right away rather than wait
        // for the next unrelated trigger (a download finishing, pausing, ...) - a no-op if the
        // limit was lowered or unchanged, or if nothing is queued.
        await _downloadManager.AdmitQueuedDownloadsAsync();

        StatusMessage = "Settings saved.";
    }
}
