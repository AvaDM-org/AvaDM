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

    /// <summary>How long the metadata call may hang before it's treated as a failed check. The
    /// shared <c>HttpClient</c> is deliberately built with <c>Timeout.InfiniteTimeSpan</c> (large
    /// downloads must not be cut off), so without this an unreachable GitHub would leave the check
    /// pending forever - and with it the "Check for Updates" button disabled for the rest of the
    /// session. Update *downloads* stay unbounded.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();
        var channel = UpdateChannelDetector.Detect();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CheckTimeout);
        var checkCt = timeoutCts.Token;

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AvaDM", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await httpClient.SendAsync(request, checkCt);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: checkCt)
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

        // For an AppImage, Environment.ProcessPath points inside the read-only FUSE mount
        // (/tmp/.mount_.../usr/bin/...) that the AppImage runtime extracts itself into - not the
        // actual .AppImage file on disk. The APPIMAGE env var (see UpdateChannelDetector) holds
        // that real, writable path, which is what needs to be replaced.
        var exePath = update.Channel == UpdateChannel.LinuxAppImage
            ? Environment.GetEnvironmentVariable("APPIMAGE")
                ?? throw new InvalidOperationException("Couldn't determine the running AppImage's path (APPIMAGE env var not set).")
            : Environment.ProcessPath
                ?? throw new InvalidOperationException("Couldn't determine the running executable's path.");
        var installDir = Path.GetDirectoryName(exePath)!;

        // Every channel below except WindowsInstaller rewrites files in installDir as this
        // (unelevated) process. Checking up front turns "the app exited and never came back" into
        // an actionable message - e.g. a portable build unzipped into Program Files or /opt, or an
        // AppImage sitting on a read-only mount.
        if (update.Channel != UpdateChannel.WindowsInstaller && !IsDirectoryWritable(installDir))
        {
            return new UpdateApplyResult(false, false,
                $"AvaDM can't update itself because it doesn't have write access to {installDir}. " +
                "Move it somewhere writable, or download the new version manually.");
        }

        // The AppImage case downloads straight into the install directory (right beside the file
        // it's about to replace) so the final swap is a same-filesystem atomic rename; the others
        // stage in a plain temp directory and only need same-filesystem guarantees once they
        // start moving files into installDir below.
        var downloadPath = update.Channel == UpdateChannel.LinuxAppImage
            ? Path.Combine(installDir, $".{update.Asset.Name}.download")
            : Path.Combine(Path.GetTempPath(), $"avadm-update-{Guid.NewGuid():N}", update.Asset.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);

        try
        {
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
                    RelaunchAfterExitAndSignal(exePath, progress);
                    return new UpdateApplyResult(true, true, null);

                case UpdateChannel.LinuxPortable:
                {
                    var stagingDir = Path.Combine(installDir, $".avadm-update-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(stagingDir);
                    try
                    {
                        // release.yml ships this as a *gzipped* tar; TarFile only reads a raw tar
                        // stream and fails with EndOfStreamException on the gzip magic bytes, so
                        // the decompression has to be explicit.
                        await using (var compressed = File.OpenRead(downloadPath))
                        await using (var tar = new GZipStream(compressed, CompressionMode.Decompress))
                            await TarFile.ExtractToDirectoryAsync(tar, stagingDir, overwriteFiles: true, ct);

                        ReplaceDirectoryContents(stagingDir, installDir);
                    }
                    finally
                    {
                        if (Directory.Exists(stagingDir))
                            Directory.Delete(stagingDir, recursive: true);
                    }
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(exePath, ExecutableFileMode);
                    TryDeleteDownloadDirectory(downloadPath);
                    RelaunchAfterExitAndSignal(exePath, progress);
                    return new UpdateApplyResult(true, true, null);
                }

                case UpdateChannel.WindowsPortable:
                {
                    var stagingDir = Path.Combine(installDir, $".avadm-update-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(stagingDir);
                    ZipFile.ExtractToDirectory(downloadPath, stagingDir, overwriteFiles: true);
                    TryDeleteDownloadDirectory(downloadPath);
                    // Hands stagingDir to a detached script that outlives this process; nothing here
                    // may delete it on the way out.
                    LaunchWindowsPortableSwapScript(stagingDir, installDir, exePath);
                    return new UpdateApplyResult(true, true, null);
                }

                case UpdateChannel.WindowsInstaller:
                {
                    // PrivilegesRequired=lowest means Setup never elevates on its own, so a
                    // per-machine install (Program Files) would fail on file copy. /ALLUSERS
                    // requests admin install mode - and therefore a UAC prompt - to match how it
                    // was originally installed. setup.iss allows this: its
                    // PrivilegesRequiredOverridesAllowed=dialog implies commandline.
                    var privilegeFlag = UpdateChannelDetector.IsWindowsPerMachineInstall()
                        ? "/ALLUSERS"
                        : "/CURRENTUSER";

                    // /AVADMRELAUNCH=1 is read by setup.iss's [Run] section: its normal launch entry
                    // carries `skipifsilent` and so never fires on a silent update, which would
                    // otherwise leave the user with no running app. Deliberately no
                    // /RESTARTAPPLICATIONS - that would race the [Run] entry and start AvaDM twice.
                    // Note /SUPPRESSMSGBOXES is plural; the singular form is silently ignored.
                    Process.Start(new ProcessStartInfo(downloadPath)
                    {
                        Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {privilegeFlag} /AVADMRELAUNCH=1",
                        UseShellExecute = true,
                    });
                    // downloadPath is deliberately left in temp: Setup is still reading it.
                    return new UpdateApplyResult(true, true, null);
                }

                default:
                    return new UpdateApplyResult(false, false, "Unsupported update channel.");
            }
        }
        catch
        {
            // Don't leave a half-written .download beside the AppImage (or a stale temp dir) for a
            // failure the user may well retry.
            TryDeleteDownloadDirectory(downloadPath);
            throw;
        }
    }

    /// <summary>The AppImage staging file lives in the install directory itself, so this deletes
    /// the file there but the whole generated directory for the temp-staged channels.</summary>
    private static void TryDeleteDownloadDirectory(string downloadPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(downloadPath)!;
            if (Path.GetFileName(directory).StartsWith("avadm-update-", StringComparison.Ordinal))
                Directory.Delete(directory, recursive: true);
            else
                File.Delete(downloadPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't clean up update staging path {DownloadPath}", downloadPath);
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".avadm-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Install directory {Directory} isn't writable, can't self-update", directory);
            return false;
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

    /// <summary>Starts the freshly-installed build, but only once this process has actually exited.
    /// The wait is not optional: <see cref="SingleInstanceService"/> holds an exclusive lock for
    /// this process's entire lifetime, so a replacement launched while we're still alive loses that
    /// lock, treats itself as a duplicate launch, signals us to come to the front and exits - and
    /// then we exit too, leaving nothing running at all. Mirrors what
    /// <see cref="LaunchWindowsPortableSwapScript"/> already does with Wait-Process on Windows.
    ///
    /// The arguments are passed to <c>sh</c> positionally rather than interpolated into the script
    /// text, so a path containing quotes or spaces can't break (or inject into) the command.</summary>
    private static void RelaunchAfterExitAndSignal(string exePath, IProgress<string>? progress)
    {
        progress?.Report("Restarting AvaDM...");

        // Bounded at ~60s so a process that somehow never exits leaves a stray shell for a minute
        // rather than forever; relaunching late is harmless either way (a second instance just
        // signals the first to the front).
        const string script = """
            i=0
            while kill -0 "$2" 2>/dev/null && [ "$i" -lt 300 ]; do sleep 0.2; i=$((i+1)); done
            exec "$1"
            """;

        Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", script, "avadm-update", exePath, Environment.ProcessId.ToString() },
            UseShellExecute = false,
        });
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
        {
            Log.Warning(
                "No {ChecksumsFileName} published for {LatestVersion} - applying {Asset} unverified",
                ChecksumsFileName, update.LatestVersion, update.Asset?.Name);
            return;
        }

        var checksumsText = await httpClient.GetStringAsync(update.ChecksumsAsset.BrowserDownloadUrl, ct);
        var expectedLine = checksumsText
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.EndsWith(update.Asset.Name, StringComparison.Ordinal));

        if (expectedLine is null)
        {
            Log.Warning(
                "{ChecksumsFileName} has no line for {Asset} - applying it unverified",
                ChecksumsFileName, update.Asset.Name);
            return;
        }

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
