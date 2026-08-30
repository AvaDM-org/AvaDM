namespace AvaDM.UI.Converters;

/// <summary>
/// Shared display-formatting logic for the downloads list: byte counts, speed, ETA, and the
/// chunk byte-range text. These are plain static helpers rather than <c>IValueConverter</c>s -
/// view models call them directly from computed properties (recomputed via
/// <c>[NotifyPropertyChangedFor]</c> on the raw values they derive from), which keeps the
/// formatting testable without any XAML converter plumbing.
/// </summary>
public static class FormatHelpers
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Human-readable byte count (e.g. "512 B", "2.3 MB"). Base-1024, matches the
    /// units a user expects from a download manager rather than SI decimal units.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < ByteUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} {ByteUnits[unitIndex]}" : $"{value:N1} {ByteUnits[unitIndex]}";
    }

    /// <summary>Human-readable throughput, e.g. "1.4 MB/s". "-" when no speed is known
    /// (nothing in flight, or the row isn't live).</summary>
    public static string FormatSpeed(double? bytesPerSecond) =>
        bytesPerSecond is { } speed and > 0 ? $"{FormatBytes((long)speed)}/s" : "-";

    /// <summary>Estimated time remaining from bytes-remaining and current speed, e.g. "2m 15s".
    /// "-" when it can't be estimated (no/zero speed); "0s" once nothing is left.</summary>
    public static string FormatEta(long bytesRemaining, double? bytesPerSecond)
    {
        if (bytesRemaining <= 0)
            return "0s";

        if (bytesPerSecond is not ({ } speed and > 0))
            return "-";

        var seconds = bytesRemaining / speed;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "-";

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    /// <summary>Byte-range text for a chunk row, e.g. "[0 B-159.0 MB]". Uses the same dynamic
    /// unit formatting as <see cref="FormatBytes"/> on each end rather than raw byte counts, and
    /// keeps the <c>[start-end]</c> bracket convention from
    /// <c>AvaDM.Console/DownloadDashboard.cs</c>'s <c>FormatChunkLine</c>. An <paramref name="endByte"/>
    /// before <paramref name="startByte"/> is the sentinel a no-<c>Content-Length</c> download's
    /// sole chunk starts with (see <c>Downloader</c>'s unknown-size fallback) - shown as "???"
    /// rather than the misleading "0 B" <see cref="FormatBytes"/> would clamp a negative end to.</summary>
    public static string FormatByteRange(long startByte, long endByte) =>
        endByte < startByte
            ? $"[{FormatBytes(startByte)}-???]"
            : $"[{FormatBytes(startByte)}-{FormatBytes(endByte)}]";
}
