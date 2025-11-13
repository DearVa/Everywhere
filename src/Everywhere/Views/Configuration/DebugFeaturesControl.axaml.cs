using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Microsoft.Extensions.Logging;
using ShadUI;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Everywhere.Views.Configuration;

public partial class DebugFeaturesControl(ToastManager toastManager, ILogger<DebugFeaturesControl> logger)
    : TemplatedControl
{
    [RelayCommand]
    private static async Task EditSettingsFileAsync()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Everywhere",
                "settings.json");

            var launched = await ServiceLocator.Resolve<ILauncher>().LaunchFileInfoAsync(new FileInfo(settingsPath));
            if (!launched)
            {
                throw new InvalidOperationException($"Unable to launch: {settingsPath}");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            ServiceLocator.Resolve<ILogger<DebugFeaturesControl>>().LogError(ex, "Failed to open settings file.");
            ServiceLocator.Resolve<ToastManager>()
                .CreateToast(LocaleKey.Common_Error.I18N())
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
            var logsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Everywhere",
                "logs");
            if (!Directory.Exists(logsPath))
            {
                Directory.CreateDirectory(logsPath);
            }


            var launched = await ServiceLocator.Resolve<ILauncher>().LaunchDirectoryInfoAsync(new DirectoryInfo(logsPath));
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
                .CreateToast(LocaleKey.Common_Error.I18N())
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }
}
