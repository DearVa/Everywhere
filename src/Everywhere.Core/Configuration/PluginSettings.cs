using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;

namespace Everywhere.Configuration;

public partial class PluginSettings : ObservableObject
{
    /// <summary>
    /// Gets or sets whether each plugin is enabled.
    /// </summary>
    /// <remarks>
    /// The key is in the format of "PluginKey.FunctionName".
    /// Plugins are disabled if the key is not present.
    /// But Functions are enabled by default if the plugin is enabled.
    /// </remarks>
    public ObservableDictionary<string, bool> IsEnabledRecords { get; set; } = new();

    /// <summary>
    /// Gets or sets the granted permissions for each plugin function.
    /// The key is in the format of "PluginKey.FunctionName.[Id]".
    /// </summary>
    public ObservableDictionary<string, ChatFunctionPermissions> GrantedPermissions { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of MCP chat plugins.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<McpChatPluginEntity> McpChatPlugins { get; set; } = [];

    /// <summary>
    /// Gets or sets the web search engine settings.
    /// </summary>
    public WebSearchEngineSettings WebSearchEngine { get; set; } = new();

    public PluginSettings()
    {
        IsEnabledRecords.CollectionChanged += delegate { OnPropertyChanged(nameof(IsEnabledRecords)); };
        GrantedPermissions.CollectionChanged += delegate { OnPropertyChanged(nameof(GrantedPermissions)); };
    }
}

/// <summary>
/// MCP Plugin record for serialization/IConfiguration purposes.
/// </summary>
[Serializable]
public partial class McpChatPluginEntity : ObservableObject
{
    public Guid Id { get; set; }

    [ObservableProperty]
    public partial StdioMcpTransportConfiguration? Stdio { get; set; }

    [ObservableProperty]
    public partial HttpMcpTransportConfiguration? Http { get; set; }

    [JsonConstructor]
    public McpChatPluginEntity() { }

    public McpChatPluginEntity(McpChatPlugin mcpChatPlugin)
    {
        Id = mcpChatPlugin.Id;
        Stdio = mcpChatPlugin.TransportConfiguration as StdioMcpTransportConfiguration;
        Http = mcpChatPlugin.TransportConfiguration as HttpMcpTransportConfiguration;
    }

    public McpChatPlugin? ToMcpChatPlugin()
    {
        var transportConfiguration = Stdio as McpTransportConfiguration ?? Http;
        if (transportConfiguration == null)
        {
            return null;
        }

        return new McpChatPlugin(Id, transportConfiguration);
    }
}