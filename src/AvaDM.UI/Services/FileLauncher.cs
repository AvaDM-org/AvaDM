using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AvaDM.UI.Services;

/// <summary>
/// Best-effort "reveal this download" launcher for the downloads list: opens a completed file in
/// its OS-default app, or reveals a file's containing folder in the OS file manager. Mirrors
/// <see cref="AvaDM.Core.Diagnostics.CrashReporter"/>'s per-OS <c>Process.Start</c> branches
/// (that class predates this one and isn't about downloads, so this is a separate, reusable copy
/// rather than a shared dependency from AvaDM.Core into AvaDM.UI).
/// </summary>
public static class FileLauncher
{
    /// <summary>Opens <paramref name="path"/> in its OS-default application. Returns false without
    /// throwing if the file no longer exists or the OS couldn't launch a handler for it - callers
    /// surface that as a toast rather than a crash.</summary>
    public static bool OpenFile(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", $"\"{path}\"");
            else
                Process.Start("xdg-open", $"\"{path}\"");

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reveals <paramref name="path"/>'s containing folder in the OS file manager,
    /// selecting the file itself where the platform supports it (Windows Explorer, macOS Finder).
    /// Falls back to just opening the directory if the file itself is missing but the directory
    /// still exists.</summary>
    public static bool OpenContainingFolder(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return false;

        var fileExists = File.Exists(path);

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start("explorer.exe", fileExists ? $"/select,\"{path}\"" : $"\"{directory}\"");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start("open", fileExists ? $"-R \"{path}\"" : $"\"{directory}\"");
            else
                // No universal "select this file" affordance across Linux file managers - open
                // the containing folder instead, same as AvaDM.Core.Diagnostics.CrashReporter.
                Process.Start("xdg-open", $"\"{directory}\"");

            return true;
        }
        catch
        {
            return false;
        }
    }
}
