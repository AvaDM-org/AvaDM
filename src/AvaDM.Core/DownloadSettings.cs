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
}
