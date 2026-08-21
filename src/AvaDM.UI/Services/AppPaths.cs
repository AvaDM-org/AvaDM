using Serilog;

namespace AvaDM.UI.Services;

/// <summary>
/// Where AvaDM actually lives on disk, for the cases where something *outside* this process has to
/// start it again later - the OS autostart entry (<see cref="AutoStartService"/>), the Linux
/// applications-menu entry (<see cref="DesktopShortcutService"/>), and the relaunch/self-replace
/// paths in <see cref="UpdateService"/>.
///
/// <see cref="Environment.ProcessPath"/> is the obvious answer and the right one everywhere except
/// an AppImage: there, the AppImage runtime mounts a squashfs image under <c>/tmp/.mount_XXXXXX</c>
/// and executes the app from inside it, so <c>ProcessPath</c> is a path that only exists while this
/// process is alive. Writing that into a login-autostart or menu entry produces exactly the failure
/// this class exists to prevent - "Could not find the program '/tmp/.mount_.../usr/bin/AvaDM.UI'"
/// after the next reboot. The <c>APPIMAGE</c> env var holds the real, persistent path of the
/// <c>.AppImage</c> file the user launched, which is what such an entry must point at.
/// </summary>
public static class AppPaths
{
    /// <summary>The path an OS-level entry (autostart, menu shortcut, relaunch) should invoke to
    /// start AvaDM. Null only if the runtime can't report an executable path at all.
    ///
    /// Under <c>dotnet run</c>/<c>dotnet AvaDM.UI.dll</c> during development this resolves to the
    /// <c>dotnet</c> host itself with no dll argument, so an entry registered from a dev build
    /// won't relaunch correctly; that's an accepted limitation of a dev workflow, not something
    /// end users hit.</summary>
    public static string? LaunchExecutablePath => AppImagePath ?? Environment.ProcessPath;

    /// <summary>The <c>.AppImage</c> file this process was launched from, or null when not running
    /// as an AppImage. The existence check guards the (unlikely, but cheap to rule out) case of a
    /// stale <c>APPIMAGE</c> inherited from some other AppImage's environment.</summary>
    public static string? AppImagePath =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path
        && File.Exists(path)
            ? path
            : null;

    /// <summary>Copies the bundled PNG logo to the per-user hicolor icon theme and returns that
    /// path, for the freedesktop entries to reference. The copy matters for the same reason
    /// <see cref="LaunchExecutablePath"/> does: the bundled icon that ships next to the executable
    /// lives inside the AppImage's temporary mount, so an <c>Icon=</c> line pointing at it goes
    /// stale the moment AvaDM exits. Returns null (caller falls back to the bare <c>avadm</c> icon
    /// name, which resolves for the .deb and for AppImages the desktop has integrated) if the
    /// bundled icon is missing or the copy fails.</summary>
    public static string? EnsureLinuxIconPath()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        // Cached because callers rebuild their .desktop contents to compare them against what's on
        // disk (see AutoStartService.RefreshIfStale), and re-copying the file for each of those
        // comparisons would be pointless I/O.
        if (_linuxIconPath is not null)
            return _linuxIconPath;

        try
        {
            // Published by AvaDM.UI.csproj next to the executable as "avadm-logo.png"; under an
            // AppImage that's inside the read-only mount, which is fine to read from.
            var source = Path.Combine(AppContext.BaseDirectory, "avadm-logo.png");
            if (!File.Exists(source))
                return null;

            var target = Path.Combine(GetDataHome(), "icons", "hicolor", "256x256", "apps", "avadm.png");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            return _linuxIconPath = target;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't install the application icon");
            return null;
        }
    }

    private static string? _linuxIconPath;

    /// <summary><c>$XDG_DATA_HOME</c>, defaulting to <c>~/.local/share</c> per the XDG base
    /// directory spec.</summary>
    public static string GetDataHome()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrEmpty(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : dataHome;
    }

    /// <summary><c>$XDG_CONFIG_HOME</c>, defaulting to <c>~/.config</c> per the XDG base directory
    /// spec.</summary>
    public static string GetConfigHome()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrEmpty(configHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : configHome;
    }
}
