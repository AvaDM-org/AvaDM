using Serilog;

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
///
/// Both the <c>Exec=</c> and <c>Icon=</c> lines have to point at paths that outlive this process,
/// which is why they come from <see cref="AppPaths"/> rather than
/// <see cref="Environment.ProcessPath"/> and the executable's own directory - under an AppImage
/// both of those sit inside a temporary mount (see <see cref="AppPaths"/>).
/// <see cref="RefreshIfStale"/> repairs an entry that has since gone stale.
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
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't check for the desktop shortcut");
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

            var exePath = AppPaths.LaunchExecutablePath;
            if (exePath is null)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildDesktopEntry(exePath));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't {Action} the desktop shortcut", created ? "create" : "remove");
            return false;
        }
    }

    /// <summary>Rewrites an existing menu entry when it no longer matches what this build would
    /// write - most importantly when it still points at an AppImage mount path that only existed
    /// for the lifetime of the process that wrote it, which is what makes the menu item fail with
    /// "Could not find the program '/tmp/.mount_.../usr/bin/AvaDM.UI'". Called once at startup so
    /// the entry heals itself rather than needing a manual toggle in Settings. Does nothing when
    /// no entry exists - creating one is the user's decision.</summary>
    public static void RefreshIfStale()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var exePath = AppPaths.LaunchExecutablePath;
            if (exePath is null || !IsCreated())
                return;

            var expected = BuildDesktopEntry(exePath);
            if (File.ReadAllText(GetDesktopFilePath()) == expected)
                return;

            Log.Information("Desktop shortcut is stale - rewriting it for the current executable path");
            File.WriteAllText(GetDesktopFilePath(), expected);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't refresh the desktop shortcut");
        }
    }

    private static string BuildDesktopEntry(string exePath) =>
        $"""
         [Desktop Entry]
         Type=Application
         Name=AvaDM
         Comment=Modern cross-platform download manager
         Exec="{exePath}" %U
         Icon={AppPaths.EnsureLinuxIconPath() ?? "avadm"}
         Terminal=false
         Categories=Network;FileTransfer;
         """;

    private static string GetDesktopFilePath() =>
        Path.Combine(AppPaths.GetDataHome(), "applications", DesktopFileName);
}
