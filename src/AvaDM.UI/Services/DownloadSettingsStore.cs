using System.Globalization;
using AvaDM.Core;

namespace AvaDM.UI.Services;

/// <summary>
/// Persists the Settings page's staged <see cref="DownloadSettings"/> fields (issue #35) in
/// <see cref="UiPreferencesRepository"/> so they survive a restart: <see cref="SaveAsync"/> runs when
/// Save is pressed, <see cref="LoadInto"/> once at startup before <see cref="DownloadManager"/> is
/// built. A missing, malformed, or out-of-range stored value leaves that field at its default.
///
/// <see cref="DownloadSettings.RepositoryPath"/> is deliberately not stored here: it locates the very
/// database these values live in, so it would need a separate bootstrap file.
/// </summary>
public static class DownloadSettingsStore
{
    private const string Prefix = "Settings.";
    private const string DownloadDirectoryKey = Prefix + "DownloadDirectory";
    private const string ChunkCountKey = Prefix + "ChunkCount";
    private const string SpeedLimitKey = Prefix + "SpeedLimitBytesPerSecond";
    private const string MaxRetryAttemptsKey = Prefix + "MaxRetryAttempts";
    private const string RetryBaseDelayKey = Prefix + "RetryBaseDelaySeconds";
    private const string InactivityTimeoutKey = Prefix + "InactivityTimeoutSeconds";
    private const string AutoRetryAttemptsKey = Prefix + "AutoRetryAttempts";
    private const string MaxConcurrentDownloadsKey = Prefix + "MaxConcurrentDownloads";

    public static async Task SaveAsync(UiPreferencesRepository preferences, DownloadSettings settings)
    {
        var ci = CultureInfo.InvariantCulture;
        await preferences.SetValueAsync(DownloadDirectoryKey, settings.DefaultDownloadDirectory);
        await preferences.SetValueAsync(ChunkCountKey, settings.DefaultChunkCount.ToString(ci));
        // An empty value means "no limit".
        await preferences.SetValueAsync(SpeedLimitKey, settings.DefaultSpeedLimitBytesPerSecond?.ToString(ci) ?? string.Empty);
        await preferences.SetValueAsync(MaxRetryAttemptsKey, settings.DefaultMaxRetryAttempts.ToString(ci));
        await preferences.SetValueAsync(RetryBaseDelayKey, settings.DefaultRetryBaseDelay.TotalSeconds.ToString("R", ci));
        await preferences.SetValueAsync(InactivityTimeoutKey, settings.DefaultInactivityTimeout.TotalSeconds.ToString("R", ci));
        await preferences.SetValueAsync(AutoRetryAttemptsKey, settings.DefaultAutoRetryAttempts.ToString(ci));
        await preferences.SetValueAsync(MaxConcurrentDownloadsKey, settings.DefaultMaxConcurrentDownloads.ToString(ci));
    }

    /// <summary>Applies whatever is stored onto <paramref name="settings"/>. Best-effort: an
    /// unreadable store leaves the defaults rather than blocking startup.</summary>
    public static void LoadInto(UiPreferencesRepository preferences, DownloadSettings settings)
    {
        try
        {
            var ci = CultureInfo.InvariantCulture;

            var directory = Get(DownloadDirectoryKey);
            if (!string.IsNullOrWhiteSpace(directory))
                settings.DefaultDownloadDirectory = directory;

            if (int.TryParse(Get(ChunkCountKey), NumberStyles.Integer, ci, out var chunkCount) && chunkCount >= 1)
                settings.DefaultChunkCount = chunkCount;

            var speedLimit = Get(SpeedLimitKey);
            if (speedLimit is not null)
                settings.DefaultSpeedLimitBytesPerSecond =
                    long.TryParse(speedLimit, NumberStyles.Integer, ci, out var limit) && limit > 0 ? limit : null;

            if (int.TryParse(Get(MaxRetryAttemptsKey), NumberStyles.Integer, ci, out var retries) && retries >= 0)
                settings.DefaultMaxRetryAttempts = retries;

            if (double.TryParse(Get(RetryBaseDelayKey), NumberStyles.Float, ci, out var retryDelay) && retryDelay > 0)
                settings.DefaultRetryBaseDelay = TimeSpan.FromSeconds(retryDelay);

            if (double.TryParse(Get(InactivityTimeoutKey), NumberStyles.Float, ci, out var timeout) && timeout > 0)
                settings.DefaultInactivityTimeout = TimeSpan.FromSeconds(timeout);

            if (int.TryParse(Get(AutoRetryAttemptsKey), NumberStyles.Integer, ci, out var autoRetries) && autoRetries >= 0)
                settings.DefaultAutoRetryAttempts = autoRetries;

            if (int.TryParse(Get(MaxConcurrentDownloadsKey), NumberStyles.Integer, ci, out var concurrent) && concurrent >= 1)
                settings.DefaultMaxConcurrentDownloads = concurrent;

            string? Get(string key) => preferences.GetValueAsync(key).GetAwaiter().GetResult();
        }
        catch
        {
            // Keep the code defaults.
        }
    }
}
