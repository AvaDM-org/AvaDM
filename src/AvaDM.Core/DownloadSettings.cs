namespace AvaDM.Core;

/// <summary>
/// User-configurable default settings for downloads. Plain data - no I/O, no download logic -
/// so it's cheap to bind to a settings UI and easy to serialize for persistence later. Holds no
/// state tied to any specific download; these are shared defaults that apply to downloads
/// started after a change. A console app or UI mutates this object directly; <see cref="Downloader"/>
/// only reads from it.
/// </summary>
public sealed class DownloadSettings
{
    /// <summary>
    /// Directory used to build a destination path when a download is started with no explicit
    /// path, or with a path that resolves to an existing directory. Defaults to the user's
    /// Downloads folder.
    /// </summary>
    public string DefaultDownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Number of concurrent Range-request chunks to split a file into when the server supports
    /// it and the caller didn't specify <see cref="DownloadOptions.ChunkCount"/> explicitly.
    /// </summary>
    public int DefaultChunkCount { get; set; } = 5;

    /// <summary>
    /// Speed limit (bytes per second) applied to new downloads when the caller didn't specify
    /// <see cref="DownloadOptions.InitialSpeedLimitBytesPerSecond"/> explicitly. <c>null</c>
    /// means no limit.
    /// </summary>
    public long? DefaultSpeedLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Path to the SQLite download index. <c>null</c>/empty means use the platform default
    /// (under <see cref="Environment.SpecialFolder.LocalApplicationData"/>) - see
    /// <see cref="GetResolvedRepositoryPath"/>.
    /// </summary>
    public string? RepositoryPath { get; set; }

    /// <summary>
    /// Number of times a failed chunk (or whole-file) download attempt is retried before the
    /// chunk is marked <see cref="ChunkStatus.Failed"/>. Covers transient connection errors,
    /// I/O errors, per-attempt timeouts, and HTTP 408/429/5xx responses - see
    /// <see cref="ChunkResiliencePipelineFactory"/>.
    /// </summary>
    public int DefaultMaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Delay before the first retry of a failed attempt; later retries back off exponentially
    /// from this with jitter.
    /// </summary>
    public TimeSpan DefaultRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Timeout applied to a single download attempt (connect plus that attempt's transfer), not
    /// to the whole chunk - each retry gets a fresh budget.
    /// </summary>
    public TimeSpan DefaultPerAttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Number of times <see cref="DownloadManager"/> automatically resumes a download that ended
    /// in <see cref="DownloadState.Failed"/> - e.g. once <see cref="DefaultMaxRetryAttempts"/> is
    /// exhausted on a chunk because a stall outlasted the whole retry budget. Each automatic
    /// resume continues from the <c>.avadm</c> footer, so no progress is lost between attempts.
    /// <c>0</c> disables automatic retries; a manual resume always resets the counter, so it
    /// never runs out for a user who keeps retrying by hand.
    /// </summary>
    public int DefaultAutoRetryAttempts { get; set; } = 10;

    /// <summary>Resolves <see cref="RepositoryPath"/> to a concrete file path, creating the
    /// containing directory if it doesn't exist yet. Called by <see cref="DownloadManager"/>
    /// when it opens the repository.</summary>
    public string GetResolvedRepositoryPath()
    {
        if (!string.IsNullOrWhiteSpace(RepositoryPath))
            return RepositoryPath;

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaDM");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "avadm.db");
    }
}
