using System.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the software updater by subscribing to settings changes and starting the automatic update check.
/// </summary>
/// <param name="softwareUpdater"></param>
/// <param name="settings"></param>
public class UpdaterInitializer(ISoftwareUpdater softwareUpdater, Settings settings) : IAsyncInitializer
{
    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        settings.Common.PropertyChanged += HandleCommonPropertyChanged;
        softwareUpdater.PropertyChanged += HandleSoftwareUpdaterPropertyChanged;

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(TimeSpan.FromHours(12), _cancellationTokenSource.Token);
        }

        return Task.CompletedTask;
    }

    private void HandleCommonPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommonSettings.IsAutomaticUpdateCheckEnabled)) return;

        if (settings.Common.IsAutomaticUpdateCheckEnabled)
        {
            softwareUpdater.RunAutomaticCheckInBackground(TimeSpan.FromHours(12), _cancellationTokenSource.Token);
        }
        else
        {
            _cancellationTokenSource.Cancel();
        }
    }

    private void HandleSoftwareUpdaterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ISoftwareUpdater.LastCheckTime)) return;

        var updater = sender.NotNull<ISoftwareUpdater>();
        if (updater.LastCheckTime.HasValue)
        {
            settings.Common.LastUpdateCheckTime = updater.LastCheckTime;
        }
    }
}