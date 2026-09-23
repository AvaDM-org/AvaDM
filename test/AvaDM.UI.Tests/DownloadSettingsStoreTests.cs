using AvaDM.Core;
using AvaDM.UI.Services;
using Xunit;

namespace AvaDM.UI.Tests;

public sealed class DownloadSettingsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"avadm-settings-{Guid.NewGuid():N}.db");

    private async Task<UiPreferencesRepository> NewRepositoryAsync()
    {
        var repository = new UiPreferencesRepository(_dbPath);
        await repository.InitializeAsync();
        return repository;
    }

    [Fact]
    public async Task SavedSettings_AreRestoredIntoFreshSettings()
    {
        var repository = await NewRepositoryAsync();
        await DownloadSettingsStore.SaveAsync(repository, new DownloadSettings
        {
            DefaultDownloadDirectory = "/data/dl",
            DefaultChunkCount = 8,
            DefaultSpeedLimitBytesPerSecond = 500_000,
            DefaultMaxRetryAttempts = 3,
            DefaultRetryBaseDelay = TimeSpan.FromSeconds(2.5),
            DefaultInactivityTimeout = TimeSpan.FromSeconds(45),
            DefaultAutoRetryAttempts = 0,
            DefaultMaxConcurrentDownloads = 2,
        });

        var restored = new DownloadSettings();
        DownloadSettingsStore.LoadInto(repository, restored);

        Assert.Equal("/data/dl", restored.DefaultDownloadDirectory);
        Assert.Equal(8, restored.DefaultChunkCount);
        Assert.Equal(500_000, restored.DefaultSpeedLimitBytesPerSecond);
        Assert.Equal(3, restored.DefaultMaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2.5), restored.DefaultRetryBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(45), restored.DefaultInactivityTimeout);
        Assert.Equal(0, restored.DefaultAutoRetryAttempts);
        Assert.Equal(2, restored.DefaultMaxConcurrentDownloads);
    }

    [Fact]
    public async Task ClearedSpeedLimit_StaysUnlimitedAfterRestore()
    {
        var repository = await NewRepositoryAsync();
        await DownloadSettingsStore.SaveAsync(repository, new DownloadSettings { DefaultSpeedLimitBytesPerSecond = 1000 });
        await DownloadSettingsStore.SaveAsync(repository, new DownloadSettings { DefaultSpeedLimitBytesPerSecond = null });

        var restored = new DownloadSettings { DefaultSpeedLimitBytesPerSecond = 42 };
        DownloadSettingsStore.LoadInto(repository, restored);

        Assert.Null(restored.DefaultSpeedLimitBytesPerSecond);
    }

    [Fact]
    public async Task NothingStored_LeavesDefaults()
    {
        var repository = await NewRepositoryAsync();

        var settings = new DownloadSettings();
        DownloadSettingsStore.LoadInto(repository, settings);

        Assert.Equal(new DownloadSettings().DefaultMaxConcurrentDownloads, settings.DefaultMaxConcurrentDownloads);
        Assert.Equal(new DownloadSettings().DefaultChunkCount, settings.DefaultChunkCount);
    }

    [Fact]
    public async Task InvalidStoredValues_AreIgnored()
    {
        var repository = await NewRepositoryAsync();
        await repository.SetValueAsync("Settings.MaxConcurrentDownloads", "0");
        await repository.SetValueAsync("Settings.ChunkCount", "many");
        await repository.SetValueAsync("Settings.InactivityTimeoutSeconds", "-5");

        var settings = new DownloadSettings();
        DownloadSettingsStore.LoadInto(repository, settings);

        var defaults = new DownloadSettings();
        Assert.Equal(defaults.DefaultMaxConcurrentDownloads, settings.DefaultMaxConcurrentDownloads);
        Assert.Equal(defaults.DefaultChunkCount, settings.DefaultChunkCount);
        Assert.Equal(defaults.DefaultInactivityTimeout, settings.DefaultInactivityTimeout);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(path))
                File.Delete(path);
    }
}
