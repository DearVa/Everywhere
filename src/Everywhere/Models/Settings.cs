﻿using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Attributes;
using Everywhere.Utils;
using MessagePack;

namespace Everywhere.Models;

public class SettingsBase : TrackableObject<SettingsBase>
{
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

    [IgnoreMember]
    public InternalSettings Internal { get; init; } = new();
}

public partial class CommonSettings() : SettingsBase("Common")
{
    [SettingsSelectionItem(ItemsSource = nameof(LanguageSource))]
    public string Language
    {
        get => LocaleManager.CurrentLocale ?? CultureInfo.CurrentUICulture.Name;
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
    public partial KeyboardHotkey AssistantHotkey { get; set; }

    [ObservableProperty]
    public partial bool ShowAssistantFloatingWindowWhenInput { get; set; }
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
    [IgnoreMember]
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

public partial class InternalSettings() : SettingsBase("Internal")
{
    [ObservableProperty]
    public partial bool IsImageEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsToolCallEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsWebSearchEnabled { get; set; }

    public int MaxChatAttachmentCount { get; set; } = 10;
}