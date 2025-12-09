using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.Linux.Interop;
using Microsoft.Extensions.Logging;
#if !DEBUG
using Everywhere.Utilities;
#endif

namespace Everywhere.Linux.Common;

public sealed partial class SoftwareUpdater(
    INativeHelper nativeHelper,
    IRuntimeConstantProvider runtimeConstantProvider,
    ILogger<SoftwareUpdater> logger
) : ObservableObject, ISoftwareUpdater, IDisposable
{
    private const string CustomUpdateServiceBaseUrl = "https://ghproxy.sylinko.com";
    private const string ApiUrl = $"{CustomUpdateServiceBaseUrl}/api?product=everywhere";
    private const string DownloadUrlBase = $"{CustomUpdateServiceBaseUrl}/download?product=everywhere&os=linux-x64";

    private readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "libcurl/7.64.1 r-curl/4.3.2 http/1.4.2 EverywhereUpdater" }
        }
    };
    private readonly ActivitySource _activitySource = new(typeof(SoftwareUpdater).FullName.NotNull());

#if !DEBUG
    private PeriodicTimer? _timer;
#endif

    private Task? _updateTask;
    private Asset? _latestAsset;
    private Version? _notifiedVersion;

    public Version CurrentVersion { get; } = typeof(SoftwareUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    [ObservableProperty] public partial DateTimeOffset? LastCheckTime { get; private set; }

    [ObservableProperty] public partial Version? LatestVersion { get; private set; }

    public void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default)
    {
#if !DEBUG
        _timer = new PeriodicTimer(interval);
        cancellationToken.Register(Stop);

        Task.Run(
            async () =>
            {
                // Clean up old update packages on startup.
                await CleanupOldUpdatesAsync();

                await CheckForUpdatesAsync(cancellationToken); // check immediately on start

                while (await _timer.WaitForNextTickAsync(cancellationToken))
                {
                    await CheckForUpdatesAsync(cancellationToken);
                }
            },
            cancellationToken);

        void Stop()
        {
            DisposeCollector.DisposeToDefault(ref _timer);
        }
#endif
    }

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_updateTask is not null) return;

            var response = await _httpClient.GetAsync(ApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = jsonDoc.RootElement;

            var latestTag = root.GetProperty("tag_name").GetString();
            if (latestTag is null) return;

            var versionString = latestTag.StartsWith('v') ? latestTag[1..] : latestTag;
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                logger.LogWarning("Could not parse version from tag: {Tag}", latestTag);
                return;
            }

            var assets = root.GetProperty("assets").Deserialize<List<AssetMetadata>>();
            var isInstalled = nativeHelper.IsInstalled;

            // Determine asset type and construct download URL
            var assetType = "zip";
            var assetNameSuffix = $"-Linux-x64-v{versionString}.zip";

            var assetMetadata = assets?.FirstOrDefault(a => a.Name.EndsWith(assetNameSuffix, StringComparison.OrdinalIgnoreCase));

            if (assetMetadata is not null)
            {
                _latestAsset = new Asset(
                    assetMetadata.Name,
                    assetMetadata.Digest,
                    assetMetadata.Size,
                    $"{DownloadUrlBase}&type={assetType}"
                );
            }

            LatestVersion = latestVersion > CurrentVersion ? latestVersion : null;
            if (_notifiedVersion != LatestVersion && LatestVersion is not null)
            {
                _notifiedVersion = LatestVersion;
                new LinuxNativeHelper().ShowDesktopNotificationAsync(
                    LocaleKey.SoftwareUpdater_UpdateAvailable_Toast_Message,
                    LocaleKey.Common_Info);
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Failed to check for updates.");
            LatestVersion = null;
        }

        LastCheckTime = DateTimeOffset.UtcNow;
    }

    public async Task PerformUpdateAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (_updateTask is not null)
        {
            await _updateTask;
            return;
        }

        if (LatestVersion is null || LatestVersion <= CurrentVersion || _latestAsset is not { } asset)
        {
            logger.LogDebug("No new version available to update.");
            return;
        }

        _updateTask = Task.Run(
            async () =>
            {
                using var activity = _activitySource.StartActivity();
                try
                {
                    var assetPath = await DownloadAssetAsync(asset, progress);

                    if (assetPath.EndsWith(".deb"))
                    {
                        UpdateViaInstaller(assetPath);
                    }
                    else
                    {
                        await UpdateViaPortableAsync(assetPath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Failed to perform update.");
                    throw;
                }
                finally
                {
                    _updateTask = null;
                }
            },
            cancellationToken);

        await _updateTask;
    }

    /// <summary>
    /// Cleans up old update packages from the updates directory.
    /// </summary>
    private async Task CleanupOldUpdatesAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var updatesPath = runtimeConstantProvider.EnsureWritableDataFolderPath("updates");
                if (!Directory.Exists(updatesPath)) return;

                foreach (var file in Directory.EnumerateFiles(updatesPath))
                {
                    var fileName = Path.GetFileName(file);
                    var match = VersionRegex().Match(fileName);

                    if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var fileVersion)) continue;

                    // Delete if the package version is older than or same as the current running version.
                    if (fileVersion > CurrentVersion) continue;

                    try
                    {
                        File.Delete(file);
                        logger.LogDebug("Deleted old update package: {FileName}", fileName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogInformation(ex, "Failed to delete old update package: {FileName}", fileName);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "An error occurred during old updates cleanup.");
            }
        });
    }

    private async Task<string> DownloadAssetAsync(Asset asset, IProgress<double> progress)
    {
        var installPath = runtimeConstantProvider.EnsureWritableDataFolderPath("updates");
        var assetDownloadPath = Path.Combine(installPath, asset.Name);

        var fileInfo = new FileInfo(assetDownloadPath);
        if (fileInfo.Exists)
        {
            if (fileInfo.Length == asset.Size && string.Equals(await HashFileAsync(), asset.Digest, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Asset {AssetName} already exists and is valid, skipping download.", asset.Name);
                progress.Report(1.0);
                return assetDownloadPath;
            }

            logger.LogDebug("Asset {AssetName} exists but is invalid, redownloading.", asset.Name);
        }

        var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(assetDownloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        var totalBytesRead = 0L;
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytesRead += bytesRead;
            progress.Report((double)totalBytesRead / totalBytes);
        }

        fs.Position = 0;
        if (!string.Equals("sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(fs)), asset.Digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Downloaded asset {asset.Name} hash does not match expected digest.");
        }

        return assetDownloadPath;

        async Task<string> HashFileAsync()
        {
            await using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await SHA256.HashDataAsync(fileStream);
            return "sha256:" + Convert.ToHexString(sha256);
        }
    }

    private static void UpdateViaInstaller(string packagePath)
    {
        // On Linux installers are uncommon for this project; if an executable installer/script is provided,
        // try to make it executable and run it. Otherwise, fall back to attempting to execute directly.
        try
        {
            if (!OperatingSystem.IsLinux())
                return;

            if (packagePath.EndsWith(".deb") && File.Exists("/etc/debian_version"))
            {
                // Use chmod to set executable permission
                Process.Start(new ProcessStartInfo("sudo", $"dpkg -i \"{packagePath}\"") { UseShellExecute = true })?.WaitForExit();
            }
        }
        catch
        {
            
        }

        Environment.Exit(0);
    }

    private async static Task UpdateViaPortableAsync(string archivePath)
    {
        // Use Entry Assembly location where possible; fall back to AppContext.BaseDirectory for single-file builds.
        var entryAssembly = Assembly.GetEntryAssembly();
        var exeLocation = entryAssembly?.Location;
        if (string.IsNullOrEmpty(exeLocation))
        {
            // In single-file publish, Assembly.Location may be empty; use base directory + executable name.
            var exeName = entryAssembly?.GetName().Name ?? "Everywhere.Linux";
            exeLocation = Path.Combine(AppContext.BaseDirectory, exeName);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"everywhere_update_{Guid.NewGuid():N}.sh");

        // Prepare a shell script that will wait for the app to exit, backup current installation, unpack new files and start the new binary.
        var scriptContent =
            "#!/usr/bin/env bash\n" +
            "set -e\n" +
            "EXE_LOCATION='" + exeLocation.Replace("'", "'\\''") + "'\n" +
            "ARCHIVE_PATH='" + archivePath.Replace("'", "'\\''") + "'\n" +
            "PARENT_DIR=\"$(dirname \\\"$EXE_LOCATION\\\")\"\n" +
            "EXE_NAME=\"$(basename \\\"$EXE_LOCATION\\\")\"\n\n" +
            "echo \"Waiting for the application to close...\"\n" +
            "# Try to terminate running instances by path or by name\n" +
            "pkill -f -- \"$EXE_LOCATION\" || true\n" +
            "pkill -f -- \"${EXE_NAME}\" || true\n" +
            "sleep 1\n\n" +
            "echo \"Backing up old version...\"\n" +
            "mv \"$PARENT_DIR\" \"${PARENT_DIR}_old\"\n\n" +
            "echo \"Unpacking new version...\"\n" +
            "mkdir -p \"$PARENT_DIR\"\n" +
            "if [[ \"$ARCHIVE_PATH\" == *.zip ]]; then\n" +
            "    if ! command -v unzip >/dev/null 2>&1; then\n" +
            "        echo \"unzip not available\" >&2\n" +
            "        mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"\n" +
            "        exit 1\n" +
            "    fi\n" +
            "    unzip -q \"$ARCHIVE_PATH\" -d \"$PARENT_DIR\" || { echo \"Unpack failed\" >&2; mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"; exit 1; }\n" +
            "else\n" +
            "    # assume tar.gz or tar.xz\n" +
            "    if [[ \"$ARCHIVE_PATH\" == *.tar.gz || \"$ARCHIVE_PATH\" == *.tgz ]]; then\n" +
            "        tar -xzf \"$ARCHIVE_PATH\" -C \"$PARENT_DIR\" || { echo \"Unpack failed\" >&2; mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"; exit 1; }\n" +
            "    elif [[ \"$ARCHIVE_PATH\" == *.tar.xz ]]; then\n" +
            "        tar -xJf \"$ARCHIVE_PATH\" -C \"$PARENT_DIR\" || { echo \"Unpack failed\" >&2; mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"; exit 1; }\n" +
            "    else\n" +
            "        # try unzip as fallback\n" +
            "        if command -v unzip >/dev/null 2>&1; then\n" +
            "            unzip -q \"$ARCHIVE_PATH\" -d \"$PARENT_DIR\" || { echo \"Unpack failed\" >&2; mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"; exit 1; }\n" +
            "        else\n" +
            "            echo \"Unknown archive format and no unzip available\" >&2\n" +
            "            mv \"${PARENT_DIR}_old\" \"$PARENT_DIR\"\n" +
            "            exit 1\n" +
            "        fi\n" +
            "    fi\n" +
            "fi\n\n" +
            "echo \"Cleaning up old files...\"\n" +
            "rm -rf \"${PARENT_DIR}_old\"\n\n" +
            "echo \"Starting new version...\"\n" +
            "# Ensure executable bit is set for the new binary\n" +
            "if [ -f \"$EXE_LOCATION\" ]; then\n" +
            "    chmod +x \"$EXE_LOCATION\" || true\n" +
            "    nohup \"$EXE_LOCATION\" >/dev/null 2>&1 &\n" +
            "fi\n\n" +
            "# Remove the updater script\n" +
            "rm -- \"$scriptPath\" || true\n";

        await File.WriteAllTextAsync(scriptPath, scriptContent);
        try
        {
            // Make script executable (best-effort)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("chmod", $"+x \"{scriptPath}\"") { UseShellExecute = false })?.WaitForExit();
            }

            // Start the script with bash and exit the current process to allow the updater to replace files.
            Process.Start(new ProcessStartInfo("/bin/bash", scriptPath) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            // If spawning the script failed, log and rethrow to surface the error to caller.
            throw new InvalidOperationException("Failed to start updater script.", ex);
        }

        Environment.Exit(0);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
#if !DEBUG
        _timer?.Dispose();
#endif
    }

    // Represents the full asset details including the constructed download URL.
    private record Asset(
        string Name,
        string Digest,
        long Size,
        string DownloadUrl
    );

    // Represents asset metadata deserialized from the API response.
    [Serializable]
    private record AssetMetadata(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("size")] long Size
    );

    [GeneratedRegex(@"-v(?<version>\d+\.\d+\.\d+(\.\d+)?)\.(exe|zip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex VersionRegex();
}