using Microsoft.Win32;
using Serilog;

namespace AvaDM.UI.Services;

/// <summary>
/// Figures out which of release.yml's distribution channels the running build came from, so
/// <see cref="UpdateService"/> knows which release asset to fetch and whether it's safe to
/// self-replace files (an install location AvaDM fully owns) or whether the platform's own
/// install convention (apt/dpkg, drag-to-Applications) should handle the update instead. Uses
/// OS-native signals rather than a packaging-time marker file, so release.yml doesn't need to
/// know anything about this:
///  - Windows: the Inno Setup installer registers itself under this fixed AppId in the per-user
///    uninstall registry key (see packaging/windows/setup.iss's [Setup] AppId) - its presence
///    means "installed", its absence means the portable zip.
///  - Linux AppImage: the AppImage runtime always sets the APPIMAGE env var to the mounted
///    image's own path before running the contained app.
///  - Linux .deb: dpkg records every installed package's file list under
///    /var/lib/dpkg/info/&lt;package&gt;.list - its presence means the .deb installed us.
///  - Linux portable: none of the above - the plain tar.gz, extracted wherever the user put it.
///  - macOS: only one channel is published (a .dmg containing a drag-to-Applications .app), so
///    there's nothing to distinguish.
/// </summary>
public static class UpdateChannelDetector
{
    private const string WindowsAppId = "{4B9D9F2E-6F0B-4C8A-9E7D-2E8B7C6A1F3D}";

    public static UpdateChannel Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return IsWindowsInstalled() ? UpdateChannel.WindowsInstaller : UpdateChannel.WindowsPortable;

            if (OperatingSystem.IsLinux())
            {
                if (Environment.GetEnvironmentVariable("APPIMAGE") is not null)
                    return UpdateChannel.LinuxAppImage;

                if (File.Exists("/var/lib/dpkg/info/avadm.list"))
                    return UpdateChannel.LinuxDeb;

                return UpdateChannel.LinuxPortable;
            }

            if (OperatingSystem.IsMacOS())
                return UpdateChannel.MacOsDmg;
        }
        catch (Exception ex)
        {
            // Best-effort: fall through to Unknown rather than blocking the update check over a
            // detection failure.
            Log.Warning(ex, "Couldn't detect the update channel");
        }

        return UpdateChannel.Unknown;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsWindowsInstalled() =>
        Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{WindowsAppId}_is1") is not null;
}
