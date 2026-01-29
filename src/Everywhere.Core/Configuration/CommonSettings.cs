using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Views;
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

    [DynamicResourceKey(
        LocaleKey.CommonSettings_Theme_Header,
        LocaleKey.CommonSettings_Theme_Description)]
    public ThemeMode Theme
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            App.ThemeManager.SwitchTheme(value);
        }
    }

#if WINDOWS
    [JsonIgnore]
    [HiddenSettingsItem]
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator => NativeHelper.IsAdministrator;

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_RestartAsAdministrator_Header,
        LocaleKey.CommonSettings_RestartAsAdministrator_Description)]
    [SettingsItem(IsVisibleBindingPath = $"!{nameof(IsAdministrator)}")]
    [SupportedOSPlatform("windows")]
    public SettingsControl<RestartAsAdministratorControl> RestartAsAdministrator { get; } = new();

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_IsStartupEnabled_Header,
        LocaleKey.CommonSettings_IsStartupEnabled_Description)]
    [SettingsItem(IsEnabledBindingPath = $"{nameof(IsAdministrator)} || !{nameof(IsAdministratorStartupEnabled)}")]
    [SupportedOSPlatform("windows")]
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
    [SupportedOSPlatform("windows")]
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
#else
    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_IsUserStartupEnabled_Header,
        LocaleKey.CommonSettings_IsUserStartupEnabled_Description)]
    public bool IsUserStartupEnabled
    {
        get => NativeHelper.IsUserStartupEnabled;
        set
        {
            try
            {
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
#endif

    [SettingsItems]
    [DynamicResourceKey(
        LocaleKey.ProxySettings_Header,
        LocaleKey.ProxySettings_Description)]
    public ProxySettings Proxy { get; set; } = new();

    /// <summary>
    /// Application font size.
    /// </summary>
    [SettingsIntegerItem(Min = -1, Max = 3, IsTextBoxVisible = false)]
    [DynamicResourceKey(
        LocaleKey.CommonSettings_FontSize_Header,
        LocaleKey.CommonSettings_FontSize_Description)]
    public int FontSize
    {
        get
        {
            return Dispatcher.UIThread.InvokeOnDemand(GetFontSize);

            int GetFontSize()
            {
                if (Application.Current is not { } app) return 0;

                var fontSizeM = app.Resources["FontSizeM"] as double? ?? 14;

                return fontSizeM switch
                {
                    < 14 => -1,
                    15 => 1,
                    16 => 2,
                    > 16 => 3,
                    _ => 0,
                };
            }
        }
        set
        {
            Dispatcher.UIThread.InvokeOnDemand(SetFontSize);
            OnPropertyChanged();

            void SetFontSize()
            {
                if (Application.Current is not { } app) return;

                // <system:Double x:Key="FontSizeS">12.8</system:Double>
                // <system:Double x:Key="FontSizeM">14</system:Double>
                // <system:Double x:Key="FontSizeL">16</system:Double>
                // <system:Double x:Key="FontSizeXl">20</system:Double>
                // <system:Double x:Key="FontSize2Xl">24</system:Double>
                // <system:Double x:Key="FontSize3Xl">30</system:Double>
                // <system:Double x:Key="FontSize4Xl">48</system:Double>

                // value: -1, 0(default), 1, 2, 3

                var fontSizeM = value switch
                {
                    -1 => 12d,
                    1 => 15d,
                    2 => 16d,
                    3 => 18d,
                    _ => 14d,
                };
                app.Resources["FontSizeS"] = fontSizeM * 0.914;
                app.Resources["FontSizeM"] = fontSizeM;
                app.Resources["FontSizeL"] = fontSizeM * 1.142;
                app.Resources["FontSizeXl"] = fontSizeM * 1.428;
                app.Resources["FontSize2Xl"] = fontSizeM * 1.714;
                app.Resources["FontSize3Xl"] = fontSizeM * 2.142;
                app.Resources["FontSize4Xl"] = fontSizeM * 3.428;

                var lineHeightM = fontSizeM / 2 * 3;
                app.Resources["LineHeightS"] = lineHeightM * 0.914;
                app.Resources["LineHeightM"] = lineHeightM;
                app.Resources["LineHeightL"] = lineHeightM * 1.142;
                app.Resources["LineHeightXl"] = lineHeightM * 1.428;
                app.Resources["LineHeight2Xl"] = lineHeightM * 1.714;
                app.Resources["LineHeight3Xl"] = lineHeightM * 2.142;
                app.Resources["LineHeight4Xl"] = lineHeightM * 3.428;
            }
        }
    }

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
        .CreateToast(LocaleResolver.Common_Error)
        .WithContent(ex.GetFriendlyMessage())
        .DismissOnClick()
        .OnBottomRight()
        .ShowError();
}