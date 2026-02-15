using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class DebugFeaturesControl(
    ILauncher launcher,
    ToastManager toastManager,
    ILogger<DebugFeaturesControl> logger)
    : TemplatedControl
{
    [RelayCommand]
    private async Task EditSettingsFileAsync()
    {
        try
        {
            var settingsPath = Path.Combine(RuntimeConstants.WritableDataPath, "settings.json");
            var launched = await launcher.LaunchFileInfoAsync(new FileInfo(settingsPath));
            if (!launched)
            {
                throw new InvalidOperationException($"Unable to launch: {settingsPath}");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to open settings file.");
            toastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }

    [RelayCommand]
    private async Task OpenLogsFolderAsync()
    {
        try
        {
            var logsPath = RuntimeConstants.EnsureWritableDataFolderPath("logs");
            var launched = await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(logsPath));
            if (!launched)
            {
                throw new InvalidOperationException($"Unable to launch: {logsPath}");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to open logs folder.");
            toastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }

    [RelayCommand]
    private async Task CreateDumpAsync()
    {
        try
        {
#if WINDOWS
            // Use createdump.exe to create a dump of the current process.
            var fileName = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath ?? typeof(DebugFeaturesControl).Assembly.Location) ?? ".",
                    "createdump.exe"));
            var dumpsPath = RuntimeConstants.EnsureWritableDataFolderPath("dumps");
            var dumpPath = Path.Combine(dumpsPath, $"dump_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.dmp");
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"--full -f {dumpPath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            // toast: Everywhere will freeze for a few seconds while the dump is being created.
            toastManager
                .CreateToast(LocaleResolver.Common_Warning)
                .WithContent(LocaleResolver.DebugFeaturesControl_CreateDumpToast_Content)
                .DismissOnClick()
                .OnBottomRight()
                .WithDelay(1000)
                .ShowWarning();
            await Task.Delay(500); // Give the toast time to show before freezing the UI.

            var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start createdump process. Maybe createdump.exe is missing?");
            }

            var error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(error))
            {
                logger.LogWarning("createdump error output: {Error}", error);
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                toastManager
                    .CreateToast(LocaleResolver.Common_Error)
                    .WithContent(error.Trim())
                    .DismissOnClick()
                    .OnBottomRight()
                    .ShowWarning();
                // The dump may still have been created despite the non-zero exit code.

                if (!File.Exists(dumpPath))
                {
                    return;
                }
            }

            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dumpsPath));
#endif
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to create dump.");
            toastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }
}
