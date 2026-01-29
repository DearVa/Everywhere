using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;

namespace Everywhere.Configuration;

[GeneratedSettingsItems]
public partial class WebSearchEngineSettings : ObservableObject
{
    [HiddenSettingsItem]
    public ObservableDictionary<WebSearchEngineProviderId, WebSearchEngineProvider> Providers { get; }

    [HiddenSettingsItem]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    public partial WebSearchEngineProviderId SelectedProviderId { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.WebSearchEngineProvider_Header,
        LocaleKey.WebSearchEngineProvider_Description)]
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

    public WebSearchEngineSettings()
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
                    EndPoint = new Customizable<string>("https://api.tavily.com", isDefaultValueReadonly: true)
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
                    EndPoint = new Customizable<string>("https://api.bochaai.com/v1/web-search", isDefaultValueReadonly: true)
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