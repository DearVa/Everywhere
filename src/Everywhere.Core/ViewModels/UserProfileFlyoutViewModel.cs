using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Cloud;

namespace Everywhere.ViewModels;

public partial class UserProfileFlyoutViewModel(ICloudClient cloudClient, ILauncher launcher) : ReactiveViewModelBase
{
    public ICloudClient CloudClient { get; } = cloudClient;

    [RelayCommand]
    private Task<bool> LoginAsync() =>
        CloudClient.LoginAsync();

    [RelayCommand]
    private Task LogoutAsync() =>
        CloudClient.LogoutAsync();

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
