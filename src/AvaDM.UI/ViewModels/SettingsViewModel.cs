using Avalonia;
using Avalonia.Styling;
using AvaDM.Core;
using AvaDM.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    public SettingsViewModel(
        DownloadSettings settings,
        UiPreferencesRepository uiPreferences,
        Action navigateToDownloads,
        bool closeToTray)
    {
        _settings = settings;
        _uiPreferences = uiPreferences;
        _navigateToDownloads = navigateToDownloads;

        _downloadDirectory = settings.DefaultDownloadDirectory;
        _chunkCount = settings.DefaultChunkCount;
        _speedLimitBytesPerSecond = settings.DefaultSpeedLimitBytesPerSecond;
        _repositoryPathInput = settings.RepositoryPath ?? string.Empty;
        _maxRetryAttempts = settings.DefaultMaxRetryAttempts;
        _retryBaseDelaySecondsInput = settings.DefaultRetryBaseDelay.TotalSeconds.ToString("0.##");
        _perAttemptTimeoutSecondsInput = settings.DefaultPerAttemptTimeout.TotalSeconds.ToString("0.##");
        _isDarkTheme = Application.Current!.RequestedThemeVariant == ThemeVariant.Dark;
        _closeToTray = closeToTray;
    }

    public string ResolvedRepositoryPathHint => _settings.GetResolvedRepositoryPath();

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool IsDarkSelected => IsDarkTheme;

    public bool IsLightSelected => !IsDarkTheme;

    public bool IsCloseToTraySelected => CloseToTray;

    public bool IsCloseAppSelected => !CloseToTray;

    [RelayCommand]
    private void CloseSettings() => _navigateToDownloads();

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
