using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Serilog;

namespace AvaDM.UI.Services;

/// <summary>Result of <see cref="UpdateService.CheckForUpdateAsync"/>. <see cref="Asset"/> is the
/// release asset matching the running build's <see cref="Channel"/> (null if none was published
/// or no update is available); <see cref="ChecksumsAsset"/> is the release's SHA256SUMS.txt, used
/// to verify <see cref="Asset"/> before it's applied.</summary>
public sealed record UpdateCheckResult(
    bool IsAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    UpdateChannel Channel,
    GitHubReleaseAsset? Asset,
    GitHubReleaseAsset? ChecksumsAsset);

/// <summary><see cref="Message"/> is either an error (when <see cref="Succeeded"/> is false) or
/// informational follow-up text for the user (e.g. "drag AvaDM into Applications") - it's null for
/// the common case of a self-applied update that's about to restart the app.
/// <see cref="ShouldExitApp"/> tells the caller AvaDM must exit now for the update to take
/// effect.</summary>
public sealed record UpdateApplyResult(bool Succeeded, bool ShouldExitApp, string? Message);

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("assets")] List<GitHubReleaseAsset> Assets);

/// <summary>
/// Checks GitHub Releases (release.yml publishes one per pushed vX.Y.Z tag) for a newer AvaDM
/// version and, where it's safe to, downloads and applies it in place - always a full replacement
/// of the previous build, never a binary diff/patch. "Safe" means the update is confined to files
/// inside AvaDM's own install directory, or - for an AppImage - the single AppImage file itself;
/// it never touches <c>DownloadSettings.RepositoryPath</c>'s database, which lives in the OS's
/// per-user app-data directory rather than the install directory (see CLAUDE.md's Packaging &amp;
/// CI section), so an update can never lose downloads, history, or settings. For channels this
/// process doesn't fully own the install location - a .deb tracked by dpkg, a .app dragged into
/// /Applications - it instead hands the user the platform's own way to update rather than
/// mutating files outside our control.
///
/// The `/repos/.../releases/latest` endpoint this hits never returns draft or prerelease releases
/// (that's GitHub's own behavior, not something this class filters), which lines up with
/// release.yml always publishing as a draft - nothing is offered as an update until a human
/// reviews and publishes it.
/// </summary>
public sealed class UpdateService(HttpClient httpClient)
{
    private const string RepoOwner = "AvaDM-org";
    private const string RepoName = "AvaDM";
    private const string ChecksumsFileName = "SHA256SUMS.txt";

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();
        var channel = UpdateChannelDetector.Detect();

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AvaDM", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub returned an empty release payload.");

        var latestVersion = ParseVersion(release.TagName);
        var isAvailable = latestVersion is not null && Normalize(latestVersion) > Normalize(currentVersion);

        var checksumsAsset = release.Assets.FirstOrDefault(a => a.Name == ChecksumsFileName);
        var asset = isAvailable ? FindAssetForChannel(release.Assets, channel) : null;

        Log.Information(
            "Update check: current {CurrentVersion}, latest {LatestVersion}, channel {Channel}, available={IsAvailable}, assetFound={AssetFound}",
            currentVersion, release.TagName, channel, isAvailable, asset is not null);

        return new UpdateCheckResult(
            isAvailable,
            Normalize(currentVersion).ToString(),
            latestVersion is null ? release.TagName : Normalize(latestVersion).ToString(),
            release.HtmlUrl,
            channel,
            asset,
            checksumsAsset);
    }

    public async Task<UpdateApplyResult> ApplyUpdateAsync(
        UpdateCheckResult update, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Log.Information(
            "Applying update {LatestVersion} via channel {Channel}, asset {Asset}",
            update.LatestVersion, update.Channel, update.Asset?.Name);

        try
        {
            return update.Channel switch
            {
                UpdateChannel.WindowsInstaller or UpdateChannel.WindowsPortable
                    or UpdateChannel.LinuxAppImage or UpdateChannel.LinuxPortable
                    => await SelfApplyAsync(update, progress, ct),

                UpdateChannel.MacOsDmg => await OpenDownloadForManualInstallAsync(
                    update,
                    "The new version's disk image should open in Finder - drag AvaDM into Applications, then relaunch it.",
                    ct),

                UpdateChannel.LinuxDeb => OpenReleasePage(
                    update, "Opened the release page - update the .deb through your package manager, or download it there."),

                _ => OpenReleasePage(update, "Opened the release page to download the update."),
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update apply failed for {LatestVersion} via channel {Channel}", update.LatestVersion, update.Channel);
            return new UpdateApplyResult(false, false, $"Update failed: {ex.Message}");
        }
    }

    private async Task<UpdateApplyResult> SelfApplyAsync(
        UpdateCheckResult update, IProgress<string>? progress, CancellationToken ct)
    {
        if (update.Asset is null)
            return new UpdateApplyResult(false, false, "No matching download was published for this build.");

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Couldn't determine the running executable's path.");
        var installDir = Path.GetDirectoryName(exePath)!;

        // The AppImage case downloads straight into the install directory (right beside the file
        // it's about to replace) so the final swap is a same-filesystem atomic rename; the others
        // stage in a plain temp directory and only need same-filesystem guarantees once they
        // start moving files into installDir below.
        var downloadPath = update.Channel == UpdateChannel.LinuxAppImage
            ? Path.Combine(installDir, $".{update.Asset.Name}.download")
            : Path.Combine(Path.GetTempPath(), $"avadm-update-{Guid.NewGuid():N}", update.Asset.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        progress?.Report("Downloading update...");
        await DownloadFileAsync(update.Asset.BrowserDownloadUrl, downloadPath, ct);

        progress?.Report("Verifying download...");
        await VerifyChecksumAsync(update, downloadPath, ct);

        progress?.Report("Installing update...");
        switch (update.Channel)
        {
            case UpdateChannel.LinuxAppImage:
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(downloadPath, ExecutableFileMode);
                // Same directory as exePath, so this is an atomic rename over a file the running
                // process still has open - its old inode stays valid until this process exits, so
                // it's safe to do while AvaDM is still executing.
                File.Move(downloadPath, exePath, overwrite: true);
                RelaunchAndSignalExit(exePath, progress);
                return new UpdateApplyResult(true, true, null);

            case UpdateChannel.LinuxPortable:
            {
                var stagingDir = Path.Combine(installDir, $".avadm-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                try
                {
                    await TarFile.ExtractToDirectoryAsync(downloadPath, stagingDir, overwriteFiles: true, ct);
                    ReplaceDirectoryContents(stagingDir, installDir);
                }
                finally
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                }
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(exePath, ExecutableFileMode);
                RelaunchAndSignalExit(exePath, progress);
                return new UpdateApplyResult(true, true, null);
            }

            case UpdateChannel.WindowsPortable:
            {
                var stagingDir = Path.Combine(installDir, $".avadm-update-{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);
                ZipFile.ExtractToDirectory(downloadPath, stagingDir, overwriteFiles: true);
                LaunchWindowsPortableSwapScript(stagingDir, installDir, exePath);
                return new UpdateApplyResult(true, true, null);
            }

            case UpdateChannel.WindowsInstaller:
                Process.Start(new ProcessStartInfo(downloadPath)
                {
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOX /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true,
                });
                return new UpdateApplyResult(true, true, null);

            default:
                return new UpdateApplyResult(false, false, "Unsupported update channel.");
        }
    }

    private const UnixFileMode ExecutableFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Moves every file from <paramref name="sourceDir"/> into the matching path under
    /// <paramref name="destDir"/>. Each <see cref="File.Move(string, string, bool)"/> is an
    /// atomic rename (source and destination are on the same filesystem, since sourceDir lives
    /// inside destDir) that succeeds even when the destination file is the running process's own,
    /// currently-open executable or a native library it has mapped - same reasoning as the
    /// AppImage case above.</summary>
    private static void ReplaceDirectoryContents(string sourceDir, string destDir)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, sourceFile);
            var destFile = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Move(sourceFile, destFile, overwrite: true);
        }
    }

    /// <summary>Windows won't let a running process's own exe/dll be overwritten in place, so the
    /// swap has to happen after this process exits: writes a small PowerShell script that waits
    /// for our PID to disappear, robocopies the staged files over the install directory, then
    /// relaunches - and starts it detached before returning.</summary>
    private static void LaunchWindowsPortableSwapScript(string stagingDir, string installDir, string exePath)
    {
        var pid = Environment.ProcessId;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"avadm-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, $"""
            Wait-Process -Id {pid} -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            robocopy "{stagingDir}" "{installDir}" /E /IS /IT /NFL /NDL /NJH /NJS
            Remove-Item -Recurse -Force "{stagingDir}"
            Start-Process -FilePath "{exePath}"
            """);

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void RelaunchAndSignalExit(string exePath, IProgress<string>? progress)
    {
        progress?.Report("Restarting AvaDM...");
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
    }

    /// <summary>macOS: downloads the .dmg and opens it (mounts it in Finder, same as a browser
    /// download would) rather than mounting/copying the .app bundle ourselves - swapping files
    /// inside a running .app bundle carries Gatekeeper/quarantine and permission complications
    /// this project can't yet verify end to end, so this defers to the platform's own familiar
    /// drag-to-Applications flow instead.</summary>
    private async Task<UpdateApplyResult> OpenDownloadForManualInstallAsync(
        UpdateCheckResult update, string instructions, CancellationToken ct)
    {
        if (update.Asset is null)
        {
            OpenTarget(update.ReleaseUrl);
            return new UpdateApplyResult(true, false, "Opened the release page to download the update.");
        }

        var downloadPath = Path.Combine(Path.GetTempPath(), update.Asset.Name);
        await DownloadFileAsync(update.Asset.BrowserDownloadUrl, downloadPath, ct);
        await VerifyChecksumAsync(update, downloadPath, ct);

        OpenTarget(downloadPath);
        return new UpdateApplyResult(true, false, instructions);
    }

    private static UpdateApplyResult OpenReleasePage(UpdateCheckResult update, string message)
    {
        OpenTarget(update.ReleaseUrl);
        return new UpdateApplyResult(true, false, message);
    }

    private static void OpenTarget(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = File.Create(destinationPath);
        await httpStream.CopyToAsync(fileStream, ct);
    }

    /// <summary>Best-effort: a release published before SHA256SUMS.txt existed (or one missing a
    /// line for this specific asset) doesn't block the update, it just skips verification.</summary>
    private async Task VerifyChecksumAsync(UpdateCheckResult update, string downloadedFilePath, CancellationToken ct)
    {
        if (update.ChecksumsAsset is null || update.Asset is null)
            return;

        var checksumsText = await httpClient.GetStringAsync(update.ChecksumsAsset.BrowserDownloadUrl, ct);
        var expectedLine = checksumsText
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.EndsWith(update.Asset.Name, StringComparison.Ordinal));

        if (expectedLine is null)
            return;

        var expectedHash = expectedLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

        await using var stream = File.OpenRead(downloadedFilePath);
        var actualHashBytes = await SHA256.HashDataAsync(stream, ct);
        var actualHash = Convert.ToHexString(actualHashBytes).ToLowerInvariant();

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {update.Asset.Name} - the download may be corrupted.");
    }

    private static GitHubReleaseAsset? FindAssetForChannel(List<GitHubReleaseAsset> assets, UpdateChannel channel)
    {
        var suffix = channel switch
        {
            UpdateChannel.WindowsInstaller => "-win-x64-setup.exe",
            UpdateChannel.WindowsPortable => "-win-x64-portable.zip",
            UpdateChannel.LinuxAppImage => "-linux-x86_64.AppImage",
            UpdateChannel.LinuxPortable => "-linux-x64.tar.gz",
            UpdateChannel.LinuxDeb => "-linux-amd64.deb",
            UpdateChannel.MacOsDmg => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "-osx-arm64.dmg"
                : "-osx-x64.dmg",
            _ => null,
        };

        return suffix is null ? null : assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static Version GetCurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    private static Version? ParseVersion(string tagName) =>
        Version.TryParse(tagName.TrimStart('v', 'V'), out var version) ? version : null;

    /// <summary>Drops the Revision component before comparing: MSBuild's -p:Version=X.Y.Z (used
    /// by release.yml) pads AssemblyVersion to X.Y.Z.0, but a bare 3-part Version("X.Y.Z") treats
    /// its missing Revision as -1 - so without this, the exact same version parsed from a tag
    /// would always compare as "older" than the running build's 4-part AssemblyVersion.</summary>
    private static Version Normalize(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
