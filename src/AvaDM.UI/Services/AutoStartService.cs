using Microsoft.Win32;
using Serilog;

namespace AvaDM.UI.Services;

/// <summary>
/// Registers/unregisters AvaDM to launch automatically when the user logs in, using each OS's
/// native autostart mechanism directly rather than AvaDM's own preferences store - the OS entry
/// itself is the source of truth, so <see cref="IsEnabled"/> reflects reality even if it was
/// toggled outside the app (e.g. Windows Task Manager's Startup tab, or the user deleting the
/// desktop file by hand) instead of trusting whatever <see cref="AvaDM.UI.ViewModels.SettingsViewModel"/>
/// last wrote. Branches on <see cref="OperatingSystem"/>'s IsWindows/IsLinux/IsMacOS rather than
/// <see cref="FileLauncher"/>'s <c>RuntimeInformation.IsOSPlatform</c> - those are the guard
/// clauses the CA1416 platform-compatibility analyzer recognizes for the Windows-only
/// <see cref="Registry"/> calls below. Otherwise mirrors that class's per-OS branching and
/// best-effort try/catch-returns-bool style.
///
/// The registered command line includes <c>--minimized</c> (see <see cref="App"/>'s handling of
/// <c>IClassicDesktopStyleApplicationLifetime.Args</c>) so a login-triggered launch starts hidden
/// in the tray instead of popping the main window, matching other download managers' behavior.
///
/// The command points at <see cref="AppPaths.LaunchExecutablePath"/>, not
/// <see cref="Environment.ProcessPath"/> - see that class for why the difference matters under an
/// AppImage. Because an entry can also go stale on its own (the user moves a portable build,
/// reinstalls elsewhere, or wrote the entry from an older build with the AppImage bug),
/// <see cref="RefreshIfStale"/> rewrites it at startup whenever it no longer matches reality.
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AvaDM";
    private const string LinuxDesktopFileName = "avadm.desktop";
    private const string MacLaunchAgentFileName = "com.avadm.app.plist";

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue(RunValueName) is not null;

            if (OperatingSystem.IsLinux())
                return File.Exists(GetLinuxDesktopFilePath());

            if (OperatingSystem.IsMacOS())
                return File.Exists(GetMacLaunchAgentPath());
        }
        catch (Exception ex)
        {
            // Best-effort: an unreadable registry key/autostart directory reads as "not enabled"
            // rather than surfacing a startup-time error for a non-essential setting - still
            // logged, though, so a stuck "off" reading is diagnosable after the fact.
            Log.Warning(ex, "Couldn't read the autostart entry");
        }

        return false;
    }

    /// <summary>Enables or disables autostart for the current platform. Returns false (without
    /// throwing) if the platform isn't supported or the write failed, so the caller can surface a
    /// plain error message instead of crashing Settings over a non-essential feature.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return SetEnabledWindows(enabled);

            if (OperatingSystem.IsLinux())
                return SetEnabledLinux(enabled);

            if (OperatingSystem.IsMacOS())
                return SetEnabledMac(enabled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't {Action} autostart", enabled ? "enable" : "disable");
            return false;
        }

        return false;
    }

    /// <summary>Rewrites an already-enabled autostart entry when it no longer matches what this
    /// build would write - i.e. it points at an executable that has since moved or, for a build
    /// launched as an AppImage, at a <c>/tmp/.mount_*</c> path that stopped existing the moment
    /// the writing process exited. Called once at startup so a broken entry heals itself on the
    /// next successful launch instead of requiring the user to toggle the setting off and on.
    /// Does nothing when autostart is off - re-enabling it is the user's decision, not ours.</summary>
    public static void RefreshIfStale()
    {
        try
        {
            if (!IsEnabled() || IsUpToDate())
                return;

            Log.Information("Autostart entry is stale - rewriting it for the current executable path");
            SetEnabled(true);
        }
        catch (Exception ex)
        {
            // Same best-effort contract as the rest of this class: autostart is a convenience, and
            // nothing here is worth failing startup over.
            Log.Warning(ex, "Couldn't refresh the autostart entry");
        }
    }

    /// <summary>Whether the entry on disk is byte-for-byte what <see cref="SetEnabled"/> would
    /// write right now. Comparing whole contents (rather than just the path) also picks up changes
    /// to the entry's own template between versions.</summary>
    private static bool IsUpToDate()
    {
        var exePath = AppPaths.LaunchExecutablePath;
        if (exePath is null)
            return true; // Nothing better to write - leave whatever is there alone.

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) as string == BuildWindowsCommand(exePath);
        }

        if (OperatingSystem.IsLinux())
            return File.ReadAllText(GetLinuxDesktopFilePath()) == BuildLinuxDesktopEntry(exePath);

        if (OperatingSystem.IsMacOS())
            return File.ReadAllText(GetMacLaunchAgentPath()) == BuildMacLaunchAgent(exePath);

        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SetEnabledWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
            return false;

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }

        var exePath = AppPaths.LaunchExecutablePath;
        if (exePath is null)
            return false;

        key.SetValue(RunValueName, BuildWindowsCommand(exePath));
        return true;
    }

    private static bool SetEnabledLinux(bool enabled)
    {
        var path = GetLinuxDesktopFilePath();

        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }

        var exePath = AppPaths.LaunchExecutablePath;
        if (exePath is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildLinuxDesktopEntry(exePath));
        return true;
    }

    private static bool SetEnabledMac(bool enabled)
    {
        var path = GetMacLaunchAgentPath();

        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }

        var exePath = AppPaths.LaunchExecutablePath;
        if (exePath is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildMacLaunchAgent(exePath));
        return true;
    }

    private static string BuildWindowsCommand(string exePath) => $"\"{exePath}\" --minimized";

    private static string BuildLinuxDesktopEntry(string exePath) =>
        $"""
         [Desktop Entry]
         Type=Application
         Name=AvaDM
         Comment=Start AvaDM download manager at login
         Exec="{exePath}" --minimized
         Icon={AppPaths.EnsureLinuxIconPath() ?? "avadm"}
         Terminal=false
         X-GNOME-Autostart-enabled=true
         """;

    private static string BuildMacLaunchAgent(string exePath) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>com.avadm.app</string>
             <key>ProgramArguments</key>
             <array>
                 <string>{exePath}</string>
                 <string>--minimized</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
         </dict>
         </plist>
         """;

    private static string GetLinuxDesktopFilePath() =>
        Path.Combine(AppPaths.GetConfigHome(), "autostart", LinuxDesktopFileName);

    private static string GetMacLaunchAgentPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", MacLaunchAgentFileName);
}
