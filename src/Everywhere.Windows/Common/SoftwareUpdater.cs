using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;

#if !DEBUG
using System.Text.RegularExpressions;
using Everywhere.Utilities;
#endif

namespace Everywhere.Windows.Common;

public sealed partial class SoftwareUpdater(
    INativeHelper nativeHelper,
    IRuntimeConstantProvider runtimeConstantProvider,
    ILogger<SoftwareUpdater> logger
) : ObservableObject, ISoftwareUpdater, IDisposable
{
    private const string CustomUpdateServiceBaseUrl = "https://ghproxy.sylinko.com";
    private const string ApiUrl = $"{CustomUpdateServiceBaseUrl}/api?product=everywhere";
    private const string DownloadUrlBase = $"{CustomUpdateServiceBaseUrl}/download?product=everywhere&os=win-x64";

    private readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "libcurl/7.64.1 r-curl/4.3.2 httr/1.4.2 EverywhereUpdater" }
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

            var assets = root.GetProperty("assets").Deserialize(JsonSerializerContext.Default.ListAssetMetadata);
            var isInstalled = nativeHelper.IsInstalled;

            // Determine asset type and construct download URL
            var assetType = isInstalled ? "setup" : "zip";
            var assetNameSuffix = isInstalled ?
                $"-Windows-x64-Setup-v{versionString}.exe" :
                $"-Windows-x64-v{versionString}.zip";

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
                nativeHelper.ShowDesktopNotificationAsync(
                    LocaleResolver.SoftwareUpdater_UpdateAvailable_Toast_Message,
                    LocaleResolver.Common_Info).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Failed to check for updates.");
            LatestVersion = null;
        }

        LastCheckTime = DateTimeOffset.UtcNow;
    }

    public async Task PerformUpdateAsync(IProgress<double> progress, CancellationToken cancellationToken = default)
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
                    var assetPath = await DownloadAssetAsync(asset, progress, cancellationToken);

                    if (assetPath.EndsWith(".exe"))
                    {
                        UpdateViaInstaller(assetPath);
                    }
                    else
                    {
                        await UpdateViaPortableAsync(assetPath, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.SoftwareUpdater_PerformUpdate_FailedToast_Message));
                    logger.LogError(ex, "Failed to perform update.");
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

#if !DEBUG
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
#endif

    private async Task<string> DownloadAssetAsync(Asset asset, IProgress<double> progress, CancellationToken cancellationToken = default)
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

        var response = await _httpClient.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(assetDownloadPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var totalBytesRead = 0L;
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;
            progress.Report((double)totalBytesRead / totalBytes);
        }

        fs.Position = 0;
        if (!string.Equals(
                "sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(fs, cancellationToken)),
                asset.Digest,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Downloaded asset {asset.Name} hash does not match expected digest.");
        }

        return assetDownloadPath;

        async Task<string> HashFileAsync()
        {
            await using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = await SHA256.HashDataAsync(fileStream, cancellationToken);
            return "sha256:" + Convert.ToHexString(sha256);
        }
    }

    private static void UpdateViaInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        Environment.Exit(0);
    }

    private async static Task UpdateViaPortableAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "update.bat");
        var exeLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(exeLocation)!;

        var scriptContent =
            $"""
             @echo off
             ECHO Waiting for the application to close...
             TASKKILL /IM "{Path.GetFileName(exeLocation)}" /F >nul 2>nul
             timeout /t 2 /nobreak >nul
             ECHO Backing up old version...
             ren "{currentDir}" "{Path.GetFileName(currentDir)}_old"
             ECHO Unpacking new version...
             powershell -Command "Expand-Archive -LiteralPath '{zipPath}' -DestinationPath '{currentDir}' -Force"
             IF %ERRORLEVEL% NEQ 0 (
                 ECHO Unpacking failed, restoring old version...
                 ren "{Path.Combine(Path.GetDirectoryName(currentDir)!, Path.GetFileName(currentDir) + "_old")}" "{Path.GetFileName(currentDir)}"
                 GOTO END
             )
             ECHO Cleaning up old files...
             rd /s /q "{Path.Combine(Path.GetDirectoryName(currentDir)!, Path.GetFileName(currentDir) + "_old")}"
             ECHO Starting new version...
             start "" "{exeLocation}"
             :END
             del "{scriptPath}"
             """;

        await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true, Verb = "runas" });
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

    [JsonSerializable(typeof(List<AssetMetadata>))]
    private partial class JsonSerializerContext : System.Text.Json.Serialization.JsonSerializerContext;

#if !DEBUG
    [GeneratedRegex(@"-v(?<version>\d+\.\d+\.\d+(\.\d+)?)\.(exe|zip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex VersionRegex();
#endif
}