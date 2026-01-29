using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Chat.Plugins;

[ObservableObject]
public abstract partial class ChatPlugin : KernelPlugin, IDisposable
{
    public abstract string Key { get; }

    [JsonIgnore]
    public abstract DynamicResourceKeyBase HeaderKey { get; }

    [JsonIgnore]
    public abstract DynamicResourceKeyBase DescriptionKey { get; }

    [JsonIgnore]
    public virtual LucideIconKind? Icon => null;

    /// <summary>
    /// Gets the uri or svg data of the icon.
    /// </summary>
    [JsonIgnore]
    public virtual string? BeautifulIcon => null;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the allowed permissions for the plugin.
    /// </summary>
    [ObservableProperty]
    public partial Customizable<ChatFunctionPermissions> AllowedPermissions { get; set; } =
        ChatFunctionPermissions.ScreenAccess |
        ChatFunctionPermissions.NetworkAccess |
        ChatFunctionPermissions.ClipboardAccess |
        ChatFunctionPermissions.FileRead;

    /// <summary>
    /// Gets the list of functions provided by this plugin for Binding use in the UI.
    /// </summary>
    public ReadOnlyObservableCollection<ChatFunction> Functions { get; }

    /// <summary>
    /// Gets the SettingsItems for this chat function.
    /// </summary>
    public virtual IReadOnlyList<SettingsItem>? SettingsItems => null;

    public override int FunctionCount => _functionsSource.Items.Count(f => f.IsEnabled);

    protected readonly SourceList<ChatFunction> _functionsSource = new();
    private readonly IDisposable _functionsConnection;

    protected ChatPlugin(string name) : base(name)
    {
        Functions = _functionsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(out _functionsConnection);
    }

    public IReadOnlyList<ChatFunction> GetEnabledFunctions() =>
        _functionsSource.Items.AsValueEnumerable().Where(f => f.IsEnabled).ToList();

    public override IEnumerator<KernelFunction> GetEnumerator() =>
        _functionsSource.Items.Where(f => f.IsEnabled).Select(f => f.KernelFunction).GetEnumerator();

    public override bool TryGetFunction(string name, [NotNullWhen(true)] out KernelFunction? function)
    {
        function = _functionsSource.Items.AsValueEnumerable().Where(f => f.IsEnabled).Select(f => f.KernelFunction)
            .FirstOrDefault(f => f.Name == name);
        return function is not null;
    }

    public virtual void Dispose()
    {
        _functionsSource.Dispose();
        _functionsConnection.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Chat kernel plugin implemented natively in Everywhere.
/// </summary>
/// <param name="name"></param>
public abstract class BuiltInChatPlugin(string name) : ChatPlugin(name)
{
    public override sealed string Key => $"builtin.{Name}";

    public virtual bool IsDefaultEnabled => false;
}

/// <summary>
/// Chat kernel plugin implemented with MCP.
/// </summary>
public sealed partial class McpChatPlugin : ChatPlugin, ILogger
{
    /// <summary>
    /// Represents a log entry for the MCP plugin.
    /// </summary>
    /// <param name="Timestamp"></param>
    /// <param name="Level"></param>
    /// <param name="Message"></param>
    public record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
    {
        public override string ToString()
        {
            return $"[{Level}] ({Timestamp:yyyy-MM-dd HH:mm:ss}) {Message}";
        }
    }

    /// <summary>
    /// Gets or sets the unique identifier of this MCP plugin.
    /// </summary>
    public Guid Id { get; set; }

    public override string Key => $"mcp.{Id}";

    public override DynamicResourceKey HeaderKey => new DirectResourceKey(TransportConfiguration?.Name ?? string.Empty);

    public override DynamicResourceKey DescriptionKey => new DirectResourceKey(TransportConfiguration?.Description ?? string.Empty);

    public override LucideIconKind? Icon => TransportConfiguration switch
    {
        StdioMcpTransportConfiguration => LucideIconKind.SquareTerminal,
        HttpMcpTransportConfiguration => LucideIconKind.Server,
        _ => null
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderKey))]
    [NotifyPropertyChangedFor(nameof(DescriptionKey))]
    [NotifyPropertyChangedFor(nameof(Icon))]
    public partial McpTransportConfiguration? TransportConfiguration { get; set; }

    /// <summary>
    /// For MCP plugins, we cannot get the permission of each function. So we use a default permission for all functions.
    /// </summary>
    [ObservableProperty]
    public partial ChatFunctionPermissions DefaultPermissions { get; set; } = ChatFunctionPermissions.AllAccess;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>
    /// Gets the log entries of this plugin.
    /// </summary>
    public ReadOnlyObservableCollection<LogEntry> LogEntries { get; }

    private const int MaxLogEntries = 1000;

    private readonly SourceList<LogEntry> _logEntriesSource = new();
    private readonly IDisposable _logEntriesConnection;

    /// <summary>
    /// Chat kernel plugin implemented with MCP.
    /// </summary>
    /// <param name="mcpTransportConfiguration"></param>
    public McpChatPlugin(McpTransportConfiguration mcpTransportConfiguration) : this(Guid.CreateVersion7(), mcpTransportConfiguration) { }

    /// <summary>
    /// Chat kernel plugin implemented with MCP.
    /// </summary>
    /// <param name="id">use GUID to avoid name conflicts</param>
    /// <param name="mcpTransportConfiguration"></param>
    public McpChatPlugin(Guid id, McpTransportConfiguration mcpTransportConfiguration) : base(id.ToString("N"))
    {
        Id = id;
        TransportConfiguration = mcpTransportConfiguration;

        LogEntries = _logEntriesSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(out _logEntriesConnection);
    }

    /// <summary>
    /// Sets the functions provided by this MCP plugin.
    /// </summary>
    /// <param name="functions"></param>
    public void SetFunctions(IEnumerable<McpChatFunction> functions) => _functionsSource.Edit(list =>
    {
        list.Clear();
        list.AddRange(functions);
    });

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _logEntriesSource.Edit(list =>
        {
            list.Add(new LogEntry(DateTime.Now, logLevel, message));
            if (list.Count > MaxLogEntries)
            {
                list.RemoveRange(0, list.Count - MaxLogEntries);
            }
        });
    }

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public override void Dispose()
    {
        base.Dispose();

        _logEntriesConnection.Dispose();
        _logEntriesSource.Dispose();
    }
}