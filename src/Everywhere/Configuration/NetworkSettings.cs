using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;

namespace Everywhere.Configuration;

/// <summary>
/// Provides application-wide network settings.
/// </summary>
public partial class NetworkSettings : SettingsCategory
{
    public override string Header => "Network";

    public override LucideIconKind Icon => LucideIconKind.Globe;

    [ObservableProperty]
    public partial bool IsProxyEnabled { get; set; }

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = nameof(IsProxyEnabled))]
    [SettingsStringItem(Watermark = "http://127.0.0.1:7890")]
    public partial string ProxyAddress { get; set; } = string.Empty;

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = nameof(IsProxyEnabled))]
    public partial bool BypassProxyOnLocal { get; set; } = true;

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = nameof(IsProxyEnabled))]
    [SettingsStringItem(IsMultiline = true, Height = 96)]
    public partial string ProxyBypassList { get; set; } = string.Empty;

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = nameof(IsProxyEnabled))]
    public partial bool UseProxyAuthentication { get; set; }

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = $"{nameof(IsProxyEnabled)} && {nameof(UseProxyAuthentication)}")]
    [SettingsStringItem(Watermark = "Username")]
    public partial string ProxyUsername { get; set; } = string.Empty;

    [ObservableProperty]
    [SettingsItem(IsVisibleBindingPath = $"{nameof(IsProxyEnabled)} && {nameof(UseProxyAuthentication)}")]
    [SettingsStringItem(IsPassword = true)]
    public partial string ProxyPassword { get; set; } = string.Empty;
}