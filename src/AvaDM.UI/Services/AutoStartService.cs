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

        var exePath = GetExecutablePath();
        if (exePath is null)
            return false;

        key.SetValue(RunValueName, $"\"{exePath}\" --minimized");
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

        var exePath = GetExecutablePath();
        if (exePath is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"""
             [Desktop Entry]
             Type=Application
             Name=AvaDM
             Comment=Start AvaDM download manager at login
             Exec="{exePath}" --minimized
             Icon=avadm
             Terminal=false
             X-GNOME-Autostart-enabled=true
             """);
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

        var exePath = GetExecutablePath();
        if (exePath is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
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
             """);
        return true;
    }

    private static string GetLinuxDesktopFilePath()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(configHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : configHome;
        return Path.Combine(baseDir, "autostart", LinuxDesktopFileName);
    }

    private static string GetMacLaunchAgentPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", MacLaunchAgentFileName);

    /// <summary>The path to write into the OS autostart entry. <see cref="Environment.ProcessPath"/>
    /// is the running executable for a published apphost/self-contained build - the normal way an
    /// end user runs AvaDM. Under <c>dotnet run</c>/<c>dotnet AvaDM.UI.dll</c> during development
    /// this resolves to the <c>dotnet</c> host itself with no dll argument, so autostart registered
    /// from a dev build won't relaunch correctly; that's an accepted limitation of a dev workflow,
    /// not something end users hit.</summary>
    private static string? GetExecutablePath() => Environment.ProcessPath;
}
