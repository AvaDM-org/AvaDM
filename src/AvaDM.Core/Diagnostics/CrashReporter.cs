using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AvaDM.Core.Diagnostics;

/// <summary>
/// Best-effort "report this crash" flow for a client with no server to phone home to: opens the
/// user's browser on a pre-filled GitHub "new issue" page, and reveals the current log file in
/// the OS file manager so the user can drag it into the issue body. GitHub issue URLs cap out at
/// a few thousand characters, so the body only ever carries a short template (versions, exception
/// summary) - never the log contents itself.
/// </summary>
public static class CrashReporter
{
    // TODO: set the real GitHub repo URL once AvaDM has one.
    private const string GitHubRepoUrl = "https://github.com/OWNER/REPO";

    /// <summary>Opens a pre-filled GitHub issue page for <paramref name="exception"/> and reveals
    /// the current log file so the user can attach it. Every step is best-effort and independent:
    /// a failure to launch the browser (e.g. headless environment) doesn't block revealing the
    /// log file, or vice versa.</summary>
    public static void Report(Exception exception)
    {
        TryOpenUrl(BuildIssueUrl(exception));

        var logPath = AppLogging.GetLatestLogFilePath();
        if (logPath is not null)
            TryRevealFile(logPath);
    }

    private static string BuildIssueUrl(Exception exception)
    {
        var version = typeof(CrashReporter).Assembly.GetName().Version;
        var title = $"Crash: {exception.GetType().Name}: {exception.Message}";
        var body = $"""
            **AvaDM version:** {version}
            **OS:** {RuntimeInformation.OSDescription}
            **.NET runtime:** {RuntimeInformation.FrameworkDescription}

            **Exception:** `{exception.GetType().FullName}`
            **Message:** {exception.Message}

            **Steps to reproduce:**
            <!-- what were you doing when this happened? -->

            **Log file:**
            <!-- drag the log file the app just opened for you into this box -->
            """;

        return $"{GitHubRepoUrl}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                // UseShellExecute doesn't resolve URLs on Linux - shell out to xdg-open directly.
                Process.Start("xdg-open", url);
            }
        }
        catch
        {
            // Best-effort: no browser, no desktop session, etc. The log file is still on disk at
            // AppLogging.LogDirectory regardless.
        }
    }

    /// <summary>Opens the log folder in the OS file manager, highlighting today's log file if
    /// one exists yet. Used by the Settings page so a user can find their logs without waiting
    /// for a crash - <see cref="Report"/> is the crash-time path, this is the on-demand one.</summary>
    public static void OpenLogFolder()
    {
        var logPath = AppLogging.GetLatestLogFilePath();
        if (logPath is not null)
            TryRevealFile(logPath);
        else
            TryRevealDirectory(AppLogging.LogDirectory);
    }

    private static void TryRevealFile(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"-R \"{path}\"");
            }
            else
            {
                // No universal "select this file" affordance across Linux file managers - open
                // the containing folder instead.
                Process.Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
            }
        }
        catch
        {
            // Best-effort, same reasoning as TryOpenUrl.
        }
    }

    private static void TryRevealDirectory(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"\"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{path}\"");
            }
            else
            {
                Process.Start("xdg-open", $"\"{path}\"");
            }
        }
        catch
        {
            // Best-effort, same reasoning as TryOpenUrl.
        }
    }
}
