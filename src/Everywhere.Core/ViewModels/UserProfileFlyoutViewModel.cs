using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;

namespace Everywhere.ViewModels;

public partial class UserProfileFlyoutViewModel(ICloudClient cloudClient, ILauncher launcher) : ReactiveViewModelBase
{
    public ICloudClient CloudClient { get; } = cloudClient;

    [RelayCommand]
    private Task<bool> LoginAsync(CancellationToken cancellationToken) =>
        CloudClient.LoginAsync(cancellationToken);

    [RelayCommand]
    private Task LogoutAsync(CancellationToken cancellationToken) =>
        CloudClient.LogoutAsync(cancellationToken);

    [RelayCommand]
    private Task<bool> ManageSubscriptionAsync() =>
        launcher.LaunchUriAsync(new Uri("https://everywhere.app/subscription"));

    [RelayCommand]
    private Task<bool> UpgradePlanAsync() =>
        launcher.LaunchUriAsync(new Uri("https://everywhere.app/pricing"));

    [RelayCommand]
    private Task<bool> OpenHelpCenterAsync() =>
        launcher.LaunchUriAsync(new Uri("https://everywhere.app/help"));

    [RelayCommand]
    private Task<bool> OpenFeedbackAsync() =>
        launcher.LaunchUriAsync(new Uri("https://github.com/DearVa/Everywhere/issues"));
}
