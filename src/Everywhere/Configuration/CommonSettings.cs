using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Views.Configuration;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public partial class CommonSettings : ObservableObject, ISettingsCategory
{
    private static INativeHelper NativeHelper => ServiceLocator.Resolve<INativeHelper>();
    private static ILogger Logger => ServiceLocator.Resolve<ILogger<CommonSettings>>();

    [HiddenSettingsItem]
    public DynamicResourceKeyBase DisplayNameKey => new DynamicResourceKey(LocaleKey.SettingsCategory_Common_Header);

    [HiddenSettingsItem]
    public LucideIconKind Icon => LucideIconKind.Box;

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial DateTimeOffset? LastUpdateCheckTime { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_SoftwareUpdate_Header,
        LocaleKey.CommonSettings_SoftwareUpdate_Description)]
    public SettingsControl<SoftwareUpdateControl> SoftwareUpdate { get; } = new();

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_IsAutomaticUpdateCheckEnabled_Header,
        LocaleKey.CommonSettings_IsAutomaticUpdateCheckEnabled_Description)]
    public partial bool IsAutomaticUpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the current application language.
    /// </summary>
    /// <remarks>
    /// Warn that this may be "default", which stands for en-US.
    /// </remarks>
    /// <example>
    /// default, zh-hans, ru, de, ja, it, fr, es, ko, zh-hant, zh-hant-hk
    /// </example>
    [DynamicResourceKey(
        LocaleKey.CommonSettings_Language_Header,
        LocaleKey.CommonSettings_Language_Description)]
    [TypeConverter(typeof(LocaleNameTypeConverter))]
    public LocaleName Language
    {
        get => LocaleManager.CurrentLocale;
        set
        {
            if (LocaleManager.CurrentLocale == value) return;
            LocaleManager.CurrentLocale = value;
            OnPropertyChanged();
        }
    }

    [HiddenSettingsItem]
    public static IEnumerable<string> ThemeSource => ["System", "Dark", "Light"];

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_Theme_Header,
        LocaleKey.CommonSettings_Theme_Description)]
    [SettingsSelectionItem(nameof(ThemeSource), I18N = true)]
    public partial string Theme { get; set; } = ThemeSource.First();

    [JsonIgnore]
    [HiddenSettingsItem]
    public static bool IsAdministrator => NativeHelper.IsAdministrator;

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_RestartAsAdministrator_Header,
        LocaleKey.CommonSettings_RestartAsAdministrator_Description)]
    [SettingsItem(IsVisibleBindingPath = $"!{nameof(IsAdministrator)}")]
    public SettingsControl<RestartAsAdministratorControl> RestartAsAdministrator { get; } = new();

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_IsStartupEnabled_Header,
        LocaleKey.CommonSettings_IsStartupEnabled_Description)]
    [SettingsItem(IsEnabledBindingPath = $"{nameof(IsAdministrator)} || !{nameof(IsAdministratorStartupEnabled)}")]
    public bool IsStartupEnabled
    {
        get => NativeHelper.IsUserStartupEnabled || NativeHelper.IsAdministratorStartupEnabled;
        set
        {
            try
            {
                // If disabling user startup while admin startup is enabled, also disable admin startup.
                if (!value && NativeHelper.IsAdministratorStartupEnabled)
                {
                    if (IsAdministrator)
                    {
                        NativeHelper.IsAdministratorStartupEnabled = false;
                        OnPropertyChanged(nameof(IsAdministratorStartupEnabled));
                    }
                    else
                    {
                        return;
                    }
                }

                NativeHelper.IsUserStartupEnabled = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Logger.LogError(ex, "Failed to set user startup enabled.");
                ShowErrorToast(ex);
            }
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_IsAdministratorStartupEnabled_Header,
        LocaleKey.CommonSettings_IsAdministratorStartupEnabled_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsStartupEnabled), IsEnabledBindingPath = nameof(IsAdministrator))]
    public bool IsAdministratorStartupEnabled
    {
        get => NativeHelper.IsAdministratorStartupEnabled;
        set
        {
            try
            {
                if (!IsAdministrator) return;

                // If enabling admin startup while user startup is disabled, also enable user startup.
                NativeHelper.IsUserStartupEnabled = !value;
                NativeHelper.IsAdministratorStartupEnabled = value;
            }
            catch (Exception ex)
            {
                ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
                Logger.LogError(ex, "Failed to set administrator startup enabled.");
                ShowErrorToast(ex);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStartupEnabled));
        }
    }

    [SettingsItems]
    [DynamicResourceKey(
        LocaleKey.ProxySettings_Header,
        LocaleKey.ProxySettings_Description)]
    public ProxySettings Proxy { get; set; } = new();

    [DynamicResourceKey(
        LocaleKey.CommonSettings_DiagnosticData_Header,
        LocaleKey.CommonSettings_DiagnosticData_Description)]
    public bool DiagnosticData
    {
        get => !Entrance.SendOnlyNecessaryTelemetry;
        set
        {
            Entrance.SendOnlyNecessaryTelemetry = !value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_DebugFeatures_Header,
        LocaleKey.CommonSettings_DebugFeatures_Description)]
    public SettingsControl<DebugFeaturesControl> DebugFeatures { get; } = new();

    private static void ShowErrorToast(Exception ex) => ServiceLocator.Resolve<ToastManager>()
        .CreateToast(LocaleKey.Common_Error.I18N())
        .WithContent(ex.GetFriendlyMessage())
        .DismissOnClick()
        .OnBottomRight()
        .ShowError();
}