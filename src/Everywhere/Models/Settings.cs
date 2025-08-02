using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Attributes;
using Everywhere.Enums;
using Everywhere.Utils;

namespace Everywhere.Models;

/// <summary>
/// This attribute is used to mark properties that should not be serialized or displayed in the UI.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
public class HiddenSettingsAttribute : Attribute;

public class SettingsBase : TrackableObject<SettingsBase>
{
    [JsonIgnore]
    public string Section { get; }

    protected SettingsBase(string section)
    {
        Section = section;
        isTrackingEnabled = true;
    }
}

[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public class Settings
{
    public CommonSettings Common { get; init; } = new();

    public BehaviorSettings Behavior { get; init; } = new();

    public ModelSettings Model { get; init; } = new();

    [HiddenSettings]
    public InternalSettings Internal { get; init; } = new();
}

public partial class CommonSettings() : SettingsBase("Common")
{
    [SettingsSelectionItem(ItemsSource = nameof(LanguageSource))]
    public string Language
    {
        get
        {
            var currentLocale = LocaleManager.CurrentLocale;
            if (currentLocale is not null) return currentLocale;

            var cultureInfo = CultureInfo.CurrentUICulture;
            while (!string.IsNullOrEmpty(cultureInfo.Name))
            {
                var nameLowered = cultureInfo.Name.ToLower();
                if (LocaleManager.AvailableLocaleNames.Contains(nameLowered))
                {
                    currentLocale = nameLowered;
                    break;
                }

                cultureInfo = cultureInfo.Parent;
            }

            return LocaleManager.CurrentLocale = currentLocale ?? "default";
        }
        set
        {
            if (LocaleManager.CurrentLocale == value) return;
            LocaleManager.CurrentLocale = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public static IEnumerable<string> LanguageSource => LocaleManager.AvailableLocaleNames;

    [ObservableProperty]
    [SettingsSelectionItem(ItemsSource = nameof(ThemeSource))]
    public partial string Theme { get; set; } = ThemeSource.First();

    public static IEnumerable<string> ThemeSource => ["System", "Dark", "Light"];
}

public partial class BehaviorSettings() : SettingsBase("Behavior")
{
    [ObservableProperty]
    public partial KeyboardHotkey AssistantHotkey { get; set; } = new(Key.E, KeyModifiers.Control | KeyModifiers.Shift);

    [ObservableProperty]
    public partial AutoPinBehavior AutoPin { get; set; } = AutoPinBehavior.Never;
}

public partial class ModelSettings() : SettingsBase("Model")
{
    [ObservableProperty]
    [SettingsStringItem(Watermark = "gpt-4o")]
    public partial string ModelName { get; set; } = "gpt-4o";

    [ObservableProperty]
    [SettingsStringItem(Watermark = "https://api.openai.com/v1")]
    public partial string Endpoint { get; set; } = "https://api.openai.com/v1";

    [ObservableProperty]
    [SettingsStringItem(Watermark = "sk-xxxxxxxxxxxxxxx", IsPassword = true)]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [SettingsDoubleItem(Min = 0.0, Max = 2.0, Step = 0.1)]
    public partial double Temperature { get; set; } = 1.0;

    [ObservableProperty]
    [SettingsDoubleItem(Min = 0.0, Max = 1.0, Step = 0.1)]
    public partial double TopP { get; set; } = 1.0;

    [ObservableProperty]
    public partial bool IsImageSupported { get; set; }

    [ObservableProperty]
    [HiddenSettings]
    public partial bool IsToolCallSupported { get; set; }

    [ObservableProperty]
    public partial bool IsWebSearchSupported { get; set; }

    [ObservableProperty]
    [SettingsGroup(nameof(IsWebSearchSupported))]
    [SettingsSelectionItem(ItemsSource = nameof(WebSearchProviders))]
    public partial string WebSearchProvider { get; set; } = "bing";

    [JsonIgnore]
    public static IEnumerable<string> WebSearchProviders => ["bing", "brave", "bocha"]; // TODO: google

    [ObservableProperty]
    [SettingsGroup(nameof(IsWebSearchSupported))]
    [SettingsStringItem(IsPassword = true)]
    public partial string WebSearchApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [SettingsGroup(nameof(IsWebSearchSupported))]
    public partial string WebSearchEndpoint { get; set; } = string.Empty;
}

[HiddenSettings]
public partial class InternalSettings() : SettingsBase("Internal")
{
    /// <summary>
    /// Used to popup welcome dialog on first launch and update.
    /// </summary>
    [ObservableProperty]
    public partial string? PreviousLaunchVersion { get; set; }

    /// <summary>
    /// Pop a tray notification when the application is launched for the first time.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFirstTimeHideToTrayIcon { get; set; } = true;

    [ObservableProperty]
    public partial bool IsImageEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsToolCallEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsWebSearchEnabled { get; set; }

    public int MaxChatAttachmentCount { get; set; } = 10;

    [ObservableProperty]
    public partial bool IsMainViewSidebarExpanded { get; set; }
}