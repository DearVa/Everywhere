using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reflection;
using System.Text;
using DynamicData;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public class ChatPluginManager : IChatPluginManager
{
    public ReadOnlyObservableCollection<BuiltInChatPlugin> BuiltInPlugins { get; }

    public ReadOnlyObservableCollection<McpChatPlugin> McpPlugins { get; }

    private readonly IRuntimeConstantProvider _runtimeConstantProvider;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChatPluginManager> _logger;

    private readonly CompositeDisposable _disposables = new();
    private readonly SourceList<BuiltInChatPlugin> _builtInPluginsSource = new();
    private readonly SourceList<McpChatPlugin> _mcpPluginsSource = new();
    private readonly ConcurrentDictionary<Guid, McpClient> _runningMcpClients = [];

    public ChatPluginManager(
        IEnumerable<BuiltInChatPlugin> builtInPlugins,
        IRuntimeConstantProvider runtimeConstantProvider,
        IWatchdogManager watchdogManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        Settings settings)
    {
        _builtInPluginsSource.AddRange(builtInPlugins);
        _runtimeConstantProvider = runtimeConstantProvider;
        _watchdogManager = watchdogManager;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ChatPluginManager>();

        // Load MCP plugins from settings.
        _mcpPluginsSource.AddRange(settings.Plugin.McpChatPlugins.Select(m => m.ToMcpChatPlugin()).OfType<McpChatPlugin>());

        // Apply the enabled state from settings.
        var isEnabledRecords = settings.Plugin.IsEnabledRecords;
        foreach (var plugin in _builtInPluginsSource.Items.AsValueEnumerable().OfType<ChatPlugin>().Concat(_mcpPluginsSource.Items))
        {
            plugin.IsEnabled = GetIsEnabled(plugin.Key, plugin is BuiltInChatPlugin { IsDefaultEnabled: true });
            foreach (var function in plugin.Functions)
            {
                function.IsEnabled = GetIsEnabled($"{plugin.Key}.{function.KernelFunction.Name}", true);
            }
        }

        BuiltInPlugins = _builtInPluginsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_builtInPluginsSource);

        McpPlugins = _mcpPluginsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_mcpPluginsSource);

        settings.Plugin.McpChatPlugins = _mcpPluginsSource
            .Connect()
            .AutoRefresh(m => m.TransportConfiguration)
            .ObserveOnDispatcher()
            .Transform(m => new McpChatPluginEntity(m), transformOnRefresh: true)
            .BindEx(_disposables);

        new ObjectObserver((in e) => HandleChatPluginChanged(_builtInPluginsSource.Items, e)).Observe(BuiltInPlugins);
        new ObjectObserver((in e) => HandleChatPluginChanged(_mcpPluginsSource.Items, e)).Observe(McpPlugins);

        // Helper method to get the enabled state from settings.
        bool GetIsEnabled(string path, bool defaultValue)
        {
            return isEnabledRecords.TryGetValue(path, out var isEnabled) ? isEnabled : defaultValue;
        }

        // Handle changes to plugins and update settings accordingly.
        void HandleChatPluginChanged<TPlugin>(IReadOnlyList<TPlugin> plugins, in ObjectObserverChangedEventArgs e) where TPlugin : ChatPlugin
        {
            if (!e.Path.EndsWith(nameof(ChatFunction.IsEnabled), StringComparison.Ordinal))
            {
                return;
            }

            var parts = e.Path.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pluginIndex) || pluginIndex < 0 || pluginIndex >= plugins.Count)
            {
                return;
            }

            var plugin = plugins[pluginIndex];
            switch (parts.Length)
            {
                case 2:
                {
                    isEnabledRecords[plugin.Key] = plugin.IsEnabled;
                    break;
                }
                case 4 when
                    int.TryParse(parts[2], out var functionIndex) &&
                    functionIndex >= 0 &&
                    functionIndex < plugin.Functions.Count:
                {
                    var function = plugin.Functions[functionIndex];
                    isEnabledRecords[$"{plugin.Key}.{function.KernelFunction.Name}"] = function.IsEnabled;
                    break;
                }
            }
        }
    }

    public McpChatPlugin CreateMcpPlugin(McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var mcpChatPlugin = new McpChatPlugin(configuration);
        _mcpPluginsSource.Add(mcpChatPlugin);
        return mcpChatPlugin;
    }

    public async Task UpdateMcpPluginAsync(McpChatPlugin mcpChatPlugin, McpTransportConfiguration configuration)
    {
        if (configuration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        var wasRunning = mcpChatPlugin.IsRunning;
        if (wasRunning)
        {
            await StopMcpClientAsync(mcpChatPlugin);
        }

        mcpChatPlugin.TransportConfiguration = configuration;

        if (wasRunning)
        {
            await StartMcpClientAsync(mcpChatPlugin, CancellationToken.None);
        }
    }

    public async Task StopMcpClientAsync(McpChatPlugin mcpChatPlugin)
    {
        if (_runningMcpClients.TryRemove(mcpChatPlugin.Id, out var runningClient))
        {
            await runningClient.DisposeAsync();
            mcpChatPlugin.IsRunning = false;
        }
    }

    public async Task RemoveMcpPluginAsync(McpChatPlugin mcpChatPlugin)
    {
        await StopMcpClientAsync(mcpChatPlugin);
        _mcpPluginsSource.Remove(mcpChatPlugin);
    }

    public async Task StartMcpClientAsync(McpChatPlugin mcpChatPlugin, CancellationToken cancellationToken)
    {
        if (mcpChatPlugin.TransportConfiguration is not { } transportConfiguration)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not set."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        if (transportConfiguration.HasErrors)
        {
            throw new HandledException(
                new InvalidOperationException("MCP transport configuration is not valid."),
                new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration));
        }

        if (_runningMcpClients.ContainsKey(mcpChatPlugin.Id)) return; // Just return without error if already running.

        var loggerFactory = new McpLoggerFactory(mcpChatPlugin, _loggerFactory);
        IClientTransport clientTransport = transportConfiguration switch
        {
            StdioMcpTransportConfiguration stdio => new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = stdio.Name,
                    Command = stdio.Command,
                    Arguments = stdio.Arguments
                        .AsValueEnumerable()
                        .Select(x => x.Value)
                        .Where(x => !x.IsNullOrWhiteSpace())
                        .ToList(),
                    WorkingDirectory = EnsureWorkingDirectory(stdio.WorkingDirectory),
                    EnvironmentVariables = EnsureLatestPath(
                        stdio.EnvironmentVariables
                            .AsValueEnumerable()
                            .Where(kv => !kv.Key.IsNullOrWhiteSpace())
                            .DistinctBy(
                                kv => kv.Key,
                                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                            .ToDictionary(
                                kv => kv.Key,
                                kv => kv.Value,
                                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)),
                },
                loggerFactory),
            HttpMcpTransportConfiguration sse => new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = sse.Name,
                    Endpoint = new Uri(sse.Endpoint, UriKind.Absolute),
                    AdditionalHeaders = sse.Headers
                        .AsValueEnumerable()
                        .Where(kv => !kv.Key.IsNullOrWhiteSpace() && !kv.Value.IsNullOrWhiteSpace())
                        .DistinctBy(kv => kv.Key)
                        .ToDictionary(kv => kv.Key, kv => kv.Value),
                    TransportMode = sse.TransportMode
                },
                _httpClientFactory.CreateClient(NetworkExtension.JsonRpcClientName),
                loggerFactory),
            _ =>
                throw new HandledException(
                    new InvalidOperationException("Unsupported MCP transport configuration type."),
                    new DynamicResourceKey(LocaleKey.ChatPluginManager_Common_InvalidMcpTransportConfiguration))
        };

        var client = await McpClient.CreateAsync(
            clientTransport,
            null,
            loggerFactory,
            cancellationToken);

        // Store the running client.
        _runningMcpClients[mcpChatPlugin.Id] = client;
        mcpChatPlugin.IsRunning = true;

        var processId = -1;
        try
        {
            if (clientTransport is StdioClientTransport)
            {
                // Add the underlying process to the watchdog to ensure it is cleaned up properly.
                // We need reflection here because StdioClientTransport does not expose the process directly.
                // client is `ModelContextProtocol.Client.McpClientImpl`
                var transportFieldInfo = client.GetType().GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic);
                var transport = transportFieldInfo?.GetValue(client); // StdioClientSessionTransport : ITransport
                var processFieldInfo = transport?.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic);
                if (processFieldInfo?.GetValue(transport) is Process { HasExited: false, Id: > 0 } process)
                {
                    process.Exited += HandleProcessExited;

                    void HandleProcessExited(object? sender, EventArgs e)
                    {
                        mcpChatPlugin.IsRunning = false;
                        _runningMcpClients.TryRemove(mcpChatPlugin.Id, out _);
                        process.Exited -= HandleProcessExited;
                    }

                    await _watchdogManager.RegisterProcessAsync(process.Id);
                    processId = process.Id;
                }
            }
        }
        finally
        {
            if (processId == -1 && transportConfiguration is StdioMcpTransportConfiguration stdio)
            {
                _logger.LogWarning(
                    "MCP started with stdio transport, but failed to get the underlying process ID for watchdog registration. " +
                    "Command: {Command}, Arguments: {Arguments}",
                    stdio.Command,
                    stdio.Arguments);
            }
        }

        mcpChatPlugin.SetFunctions(
            await client
                .EnumerateToolsAsync(cancellationToken: cancellationToken)
                .Select(t => new McpChatFunction(t))
                .ToListAsync(cancellationToken));

        string EnsureWorkingDirectory(string? workingDirectory)
        {
            if (Directory.Exists(workingDirectory)) return workingDirectory;

            // If not exists, fall back to Everywhere\cache\plugins\mcp\<plugin-id>
            var fallbackDir = _runtimeConstantProvider.EnsureWritableDataFolderPath(
                Path.Combine("plugins", "mcp", mcpChatPlugin.Id.ToString("N")));
            return fallbackDir;
        }

        Dictionary<string, string?> EnsureLatestPath(Dictionary<string, string?> environmentVariables)
        {
            var latestPath = EnvironmentVariableUtilities.GetLatestPathVariable();
            if (latestPath.IsNullOrEmpty()) return environmentVariables;

            // Put our latest PATH at the front.
            var pathBuilder = new StringBuilder(latestPath);

            // Append existing PATH variable if any.
            if (environmentVariables.TryGetValue("PATH", out var existingPath) && !existingPath.IsNullOrEmpty())
            {
                pathBuilder.Append(Path.PathSeparator).Append(existingPath);
            }

            environmentVariables["PATH"] = pathBuilder.ToString();
            return environmentVariables;
        }
    }

    public async Task<IChatPluginScope> CreateScopeAsync(CancellationToken cancellationToken)
    {
        var builtInPlugins = _builtInPluginsSource.Items.AsValueEnumerable().Where(p => p.IsEnabled).ToList();

        // Activate MCP plugins.
        var mcpPlugins = new List<McpChatPlugin>();
        foreach (var mcpPlugin in _mcpPluginsSource.Items.AsValueEnumerable().Where(p => p.IsEnabled).ToList())
        {
            try
            {
                await StartMcpClientAsync(mcpPlugin, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new HandledException(
                    ex,
                    new FormattedDynamicResourceKey(
                        LocaleKey.ChatPluginManager_Common_FailedToStartMcpPlugin,
                        new DirectResourceKey(mcpPlugin.Name)));
            }

            mcpPlugins.Add(mcpPlugin);
        }

        // Ensure that functions in the scope do not have the same name.
        var functionNames = new HashSet<string>();

        return new ChatPluginScope(
            builtInPlugins
                .AsValueEnumerable()
                .Cast<ChatPlugin>()
                .Concat(mcpPlugins)
                .Select(p => new ChatPluginSnapshot(p, functionNames))
                .ToList());
    }

    private class ChatPluginScope(List<ChatPluginSnapshot> pluginSnapshots) : IChatPluginScope
    {
        public IReadOnlyList<ChatPlugin> Plugins => pluginSnapshots;

        public bool TryGetPluginAndFunction(
            string functionName,
            [NotNullWhen(true)] out ChatPlugin? plugin,
            [NotNullWhen(true)] out ChatFunction? function,
            [NotNullWhen(false)] out IReadOnlyList<string>? similarFunctionNames)
        {
            foreach (var pluginSnapshot in pluginSnapshots)
            {
                function = pluginSnapshot.Functions.AsValueEnumerable().FirstOrDefault(f => f.KernelFunction.Name == functionName);
                if (function is not null)
                {
                    plugin = pluginSnapshot;
                    similarFunctionNames = null;
                    return true;
                }
            }

            plugin = null;
            function = null;
            similarFunctionNames = FuzzySharp.Process.ExtractTop(
                    functionName,
                    pluginSnapshots.SelectMany(p => p.Functions).Select(f => f.KernelFunction.Name),
                    limit: 5)
                .Where(r => r.Score >= 60)
                .Select(r => r.Value)
                .ToList();
            return false;
        }
    }

    private class ChatPluginSnapshot : ChatPlugin
    {
        public override string Key => _originalChatPlugin.Key;
        public override DynamicResourceKeyBase HeaderKey => _originalChatPlugin.HeaderKey;
        public override DynamicResourceKeyBase DescriptionKey => _originalChatPlugin.DescriptionKey;
        public override LucideIconKind? Icon => _originalChatPlugin.Icon;
        public override string? BeautifulIcon => _originalChatPlugin.BeautifulIcon;

        private readonly ChatPlugin _originalChatPlugin;

        public ChatPluginSnapshot(
            ChatPlugin originalChatPlugin,
            HashSet<string> functionNames) : base(originalChatPlugin.Name)
        {
            _originalChatPlugin = originalChatPlugin;
            AllowedPermissions = originalChatPlugin.AllowedPermissions.ActualValue;
            _functionsSource.AddRange(
                originalChatPlugin
                    .GetEnabledFunctions()
                    .Select(EnsureUniqueFunctionName));

            ChatFunction EnsureUniqueFunctionName(ChatFunction function)
            {
                var metadata = function.KernelFunction.Metadata;
                if (functionNames.Add(metadata.Name)) return function;

                var postfix = 1;
                string newName;
                do
                {
                    newName = $"{metadata.Name}_{postfix++}";
                }
                while (!functionNames.Add(newName));
                metadata.Name = newName;
                return function;
            }
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    /// <summary>
    /// Used to create ILogger instances for MCP clients.
    /// It logs to both the Everywhere logging system and the <see cref="McpChatPlugin"/>'s log entries.
    /// </summary>
    private sealed class McpLoggerFactory(McpChatPlugin mcpChatPlugin, ILoggerFactory innerLoggerFactory) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
            innerLoggerFactory.AddProvider(provider);
        }

        public ILogger CreateLogger(string categoryName)
        {
            var innerLogger = innerLoggerFactory.CreateLogger(categoryName);
            return new McpLogger(mcpChatPlugin, innerLogger);
        }

        public void Dispose()
        {
            innerLoggerFactory.Dispose();
        }

        private sealed class McpLogger(ILogger mcpChatPlugin, ILogger innerLogger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => innerLogger.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                mcpChatPlugin.Log(logLevel, eventId, state, exception, formatter);
                innerLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}