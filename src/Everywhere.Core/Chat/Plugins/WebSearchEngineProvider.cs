using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.Chat.Plugins;

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