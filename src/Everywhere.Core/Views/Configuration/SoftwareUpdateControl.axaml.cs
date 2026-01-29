using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class SoftwareUpdateControl(
    Settings settings,
    ISoftwareUpdater softwareUpdater,
    ToastManager toastManager,
    ILogger<SoftwareUpdateControl> logger
) : TemplatedControl
{
    public Settings Settings { get; } = settings;

    public ISoftwareUpdater SoftwareUpdater { get; } = softwareUpdater;

    public static readonly StyledProperty<DynamicResourceKeyBase?> UpdateOrCheckTitleProperty = AvaloniaProperty.Register<SoftwareUpdateControl, DynamicResourceKeyBase?>(
        nameof(UpdateOrCheckTitle));

    public DynamicResourceKeyBase? UpdateOrCheckTitle
    {
        get => GetValue(UpdateOrCheckTitleProperty);
        set => SetValue(UpdateOrCheckTitleProperty, value);
    }

    [RelayCommand]
    private async Task UpdateOrCheckAsync()
    {
        UpdateOrCheckTitle = new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_CheckingUpdateTitle_Text);
        if (SoftwareUpdater.LatestVersion is not null)
        {
            UpdateOrCheckTitle = new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_UpdatingTitle_Text);
            await PerformUpdateAsync();
            return;
        }
        
        try
        {
            await SoftwareUpdater.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_CheckForUpdatesFailed_Content));
            logger.LogError(ex, "Failed to check for updates.");
            ShowErrorToast(ex);
        }
    }

    private async Task PerformUpdateAsync()
    {
        try
        {
            var progress = new Progress<double>();
            var cts = new CancellationTokenSource();
            toastManager
                .CreateToast(LocaleResolver.Common_Info)
                .WithContent(LocaleResolver.CommonSettings_SoftwareUpdate_Toast_DownloadingUpdate)
                .WithProgress(progress)
                .WithCancellationTokenSource(cts)
                .WithDelay(0d)
                .OnBottomRight()
                .ShowInfo();
            await SoftwareUpdater.PerformUpdateAsync(progress, cts.Token);
        }
        catch (Exception ex)
        {
            ex = new HandledException(ex, new DynamicResourceKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_UpdateFailed_Content));
            logger.LogError(ex, "Failed to perform update.");
            ShowErrorToast(ex);
        }
    }

    private void ShowErrorToast(Exception ex) => toastManager
        .CreateToast(LocaleResolver.Common_Error)
        .WithContent(ex.GetFriendlyMessage())
        .DismissOnClick()
        .OnBottomRight()
        .ShowError();
}