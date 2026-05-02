using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
using Everywhere.Views;

namespace Everywhere.Configuration;

[TypeConverter(typeof(FallbackEnumConverter))]
public enum WebSearchEngineProviderId
{
    Google,
    Tavily,
    Brave,
    Bocha,
    Jina,
    UniFuncs,
    SearXNG
}

[GeneratedSettingsItems]
public sealed partial class WebSearchEngineProvider(ObservableCollection<ApiKey> apiKeys) : ObservableObject
{
    [JsonIgnore]
    [HiddenSettingsItem]
    public required WebSearchEngineProviderId Id { get; init; }

    [JsonIgnore]
    [HiddenSettingsItem]
    public string DisplayName { get; init; } = string.Empty;

    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_EndPoint_Header,
        LocaleKey.WebSearchEngineProvider_EndPoint_Description)]
    public required Customizable<string> EndPoint { get; init; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Guid ApiKey { get; set; }

    [JsonIgnore]
    [HiddenSettingsItem]
    public bool IsSearchEngineIdVisible => Id == WebSearchEngineProviderId.Google;

    [JsonIgnore]
    [HiddenSettingsItem]
    public bool IsApiKeyVisible => Id != WebSearchEngineProviderId.SearXNG;

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsApiKeyVisible))]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(apiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });

    /// <summary>
    /// for Google search engine, this is the search engine ID.
    /// </summary>
    [IgnoreDataMember]
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Header,
        LocaleKey.WebSearchEngineProvider_SearchEngineId_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsSearchEngineIdVisible))]
    public partial string? SearchEngineId { get; set; }

    public override bool Equals(object? obj) => obj is WebSearchEngineProvider provider && Id == provider.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

[GeneratedSettingsItems]
public sealed partial class WebSearchEnginePluginSettings : ObservableObject
{
    [HiddenSettingsItem]
    public ObservableDictionary<WebSearchEngineProviderId, WebSearchEngineProvider> Providers { get; }

    [HiddenSettingsItem]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    public partial WebSearchEngineProviderId SelectedProviderId { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.WebSearchEnginePluginSettings_SelectedProvider_Header,
        LocaleKey.WebSearchEnginePluginSettings_SelectedProvider_Description)]
    [SettingsItems(IsExpanded = true)]
    [SettingsSelectionItem(
        $"{nameof(Providers)}.{nameof(Providers.Values)}",
        DataTemplateKey = typeof(WebSearchEngineProvider))]
    public WebSearchEngineProvider? SelectedProvider
    {
        get => Providers.GetValueOrDefault(SelectedProviderId);
        set
        {
            if (Equals(SelectedProviderId, value?.Id)) return;
            SelectedProviderId = value?.Id ?? default;
        }
    }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ObservableCollection<ApiKey> ApiKeys { get; set; }

    public WebSearchEnginePluginSettings()
    {
        ApiKeys = [];
        Providers = new ObservableDictionary<WebSearchEngineProviderId, WebSearchEngineProvider>
        {
            {
                WebSearchEngineProviderId.Google,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.Google,
                    DisplayName = "Google",
                    EndPoint = new Customizable<string>("https://customsearch.googleapis.com", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.Tavily,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.Tavily,
                    DisplayName = "Tavily",
                    EndPoint = new Customizable<string>("https://api.tavily.com/search", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.Brave,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.Brave,
                    DisplayName = "Brave",
                    EndPoint = new Customizable<string>("https://api.search.brave.com/res/v1/web/search", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.Bocha,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.Bocha,
                    DisplayName = "Bocha",
                    EndPoint = new Customizable<string>("https://api.bocha.cn/v1/web-search", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.Jina,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.Jina,
                    DisplayName = "Jina",
                    EndPoint = new Customizable<string>("https://s.jina.ai", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.UniFuncs,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.UniFuncs,
                    DisplayName = "UniFuncs",
                    EndPoint = new Customizable<string>("https://api.unifuncs.com/api/web-search/search", isDefaultValueReadonly: true)
                }
            },
            {
                WebSearchEngineProviderId.SearXNG,
                new WebSearchEngineProvider(ApiKeys)
                {
                    Id = WebSearchEngineProviderId.SearXNG,
                    DisplayName = "SearXNG",
                    EndPoint = new Customizable<string>("https://searxng.example.com/search", isDefaultValueReadonly: true)
                }
            },
        };
    }
}