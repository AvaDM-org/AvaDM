namespace AvaDM.UI.Services;

/// <summary>
/// Creates/removes a Linux applications-menu entry (<c>~/.local/share/applications/avadm.desktop</c>).
/// This is a different file from the one <see cref="AutoStartService"/> writes under
/// <c>~/.config/autostart</c>: that one is a login-autostart hook (launched with
/// <c>--minimized</c>), this one is what makes AvaDM show up in the desktop's application
/// launcher/menu with a normal (non-minimized) launch. Packaged builds (.deb, AppImage) already
/// install an equivalent entry through their own mechanism (dpkg postinst / AppImage desktop
/// integration) - this service exists for the plain tar.gz portable build, which has no installer
/// to do that for it. Windows and macOS don't need this: their installers create the shortcut/
/// Applications-folder entry directly, so this is a no-op there (callers should check
/// <see cref="OperatingSystem.IsLinux"/> before showing any UI for it).
/// </summary>
public static class DesktopShortcutService
{
    private const string DesktopFileName = "avadm.desktop";

    public static bool IsCreated()
    {
        try
        {
            return OperatingSystem.IsLinux() && File.Exists(GetDesktopFilePath());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates or removes the menu entry. Returns false (without throwing) if not on
    /// Linux or the write failed, so the caller can surface a plain error message instead of
    /// crashing Settings over a non-essential feature.</summary>
    public static bool SetCreated(bool created)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        try
        {
            var path = GetDesktopFilePath();

            if (!created)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }

            var exePath = Environment.ProcessPath;
            if (exePath is null)
                return false;

            var iconPath = Path.Combine(
                Path.GetDirectoryName(exePath) ?? string.Empty, "avadm-logo.png");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"""
                 [Desktop Entry]
                 Type=Application
                 Name=AvaDM
                 Comment=Modern cross-platform download manager
                 Exec="{exePath}" %U
                 Icon={(File.Exists(iconPath) ? iconPath : "avadm")}
                 Terminal=false
                 Categories=Network;FileTransfer;
                 """);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDesktopFilePath()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = string.IsNullOrEmpty(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : dataHome;
        return Path.Combine(baseDir, "applications", DesktopFileName);
    }
}
