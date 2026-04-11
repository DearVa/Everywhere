using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins.McpExtensions;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public class ChatPluginManager : IChatPluginManager
{
    public ReadOnlyObservableCollection<BuiltInChatPlugin> BuiltInPlugins { get; }

    public ReadOnlyObservableCollection<McpChatPlugin> McpPlugins { get; }

    private readonly IWatchdogManager _watchdogManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Settings _settings;
    private readonly ILogger<ChatPluginManager> _logger;

    private readonly CompositeDisposable _disposables = new();
    private readonly SourceList<BuiltInChatPlugin> _builtInPluginsSource = new();
    private readonly SourceList<McpChatPlugin> _mcpPluginsSource = new();
    private readonly ConcurrentDictionary<Guid, ManagedMcpClient> _runningMcpClients = [];

    public ChatPluginManager(
        IEnumerable<BuiltInChatPlugin> builtInPlugins,
        IWatchdogManager watchdogManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        Settings settings)
    {
        _builtInPluginsSource.AddRange(builtInPlugins);
        _watchdogManager = watchdogManager;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _settings = settings;
        _logger = loggerFactory.CreateLogger<ChatPluginManager>();

        // Load MCP plugins from settings.
        _mcpPluginsSource.AddRange(settings.Plugin.McpChatPlugins.Select(m => m.ToMcpChatPlugin()).OfType<McpChatPlugin>());

        // Apply the enabled state from settings.
        var isEnabledRecords = settings.Plugin.IsEnabledRecords;
        var isPermissionGrantedRecords = settings.Plugin.IsPermissionGrantedRecords;
        var pluginKeys = new HashSet<string>();
        foreach (var plugin in _builtInPluginsSource.Items.AsValueEnumerable().OfType<ChatPlugin>().Concat(_mcpPluginsSource.Items))
        {
            pluginKeys.Add(plugin.Key);
            plugin.IsEnabled = GetIsEnabled(plugin.Key, plugin is BuiltInChatPlugin { IsDefaultEnabled: true });
            foreach (var function in plugin.Functions)
            {
                var key = $"{plugin.Key}.{function.KernelFunction.Name}";
                function.IsEnabled = GetIsEnabled(key, true);
                function.AutoApprove = function.IsAutoApproveAllowed && GetIsPermissionGranted(key, function.Permissions);
            }
        }

        // Remove any records in settings that do not correspond to any existing plugin.
        foreach (var key in isEnabledRecords.Keys.AsValueEnumerable().ToList())
        {
            if (pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal)))
            {
                isEnabledRecords.Remove(key);
            }
        }
        foreach (var key in isPermissionGrantedRecords.Keys.AsValueEnumerable().ToList())
        {
            if (pluginKeys.All(k => k != key && !key.StartsWith($"{k}.", StringComparison.Ordinal)))
            {
                isPermissionGrantedRecords.Remove(key);
            }
        }

        BuiltInPlugins = _builtInPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_builtInPluginsSource);

        McpPlugins = _mcpPluginsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_mcpPluginsSource);

        settings.Plugin.McpChatPlugins = _mcpPluginsSource
            .Connect()
            .AutoRefresh(m => m.TransportConfiguration)
            .ObserveOnAvaloniaDispatcher()
            .Transform(m => new McpChatPluginEntity(m), transformOnRefresh: true)
            .BindEx(_disposables);

        new ObjectObserver((in e) => HandleChatPluginChanged(_builtInPluginsSource.Items, e)).Observe(BuiltInPlugins);
        new ObjectObserver((in e) => HandleChatPluginChanged(_mcpPluginsSource.Items, e)).Observe(McpPlugins);

        // Helper method to get the enabled state from settings.
        bool GetIsEnabled(string path, bool defaultValue)
        {
            return isEnabledRecords.TryGetValue(path, out var isEnabled) ? isEnabled : defaultValue;
        }

        bool GetIsPermissionGranted(string path, ChatFunctionPermissions permissions)
        {
            if (isPermissionGrantedRecords.TryGetValue(path, out var isGranted) && !isGranted) return false;
            if (isGranted) return true;
            return permissions <= ChatFunctionPermissions.AutoGranted;
        }

        // Handle changes to plugins and update settings accordingly.
        void HandleChatPluginChanged<TPlugin>(IReadOnlyList<TPlugin> plugins, in ObjectObserverChangedEventArgs e) where TPlugin : ChatPlugin
        {
            var parts = e.Path.Split(':');
            if (parts.Length < 2 || !int.TryParse(parts[0], out var pluginIndex) || pluginIndex < 0 || pluginIndex >= plugins.Count)
            {
                return;
            }

            var plugin = plugins[pluginIndex];
            var value = e.Value is true;

            ObservableDictionary<string, bool> records;
            bool? defaultValue;
            if (e.Path.EndsWith(nameof(ChatFunction.IsEnabled), StringComparison.Ordinal))
            {
                records = isEnabledRecords;
                defaultValue = parts.Length != 2 || plugin is BuiltInChatPlugin { IsDefaultEnabled: true };
            }
            else if (e.Path.EndsWith(nameof(ChatFunction.AutoApprove), StringComparison.Ordinal))
            {
                records = isPermissionGrantedRecords;
                defaultValue = null;
            }
            else
            {
                return;
            }

            string key;
            switch (parts.Length)
            {
                case 2:
                {
                    key = plugin.Key;
                    break;
                }
                case 4 when
                    int.TryParse(parts[2], out var functionIndex) &&
                    functionIndex >= 0 &&
                    functionIndex < plugin.Functions.Count:
                {
                    var function = plugin.Functions[functionIndex];
                    key = $"{plugin.Key}.{function.KernelFunction.Name}";
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Unexpected change path: {e.Path}");
                }
            }

            if (value == defaultValue) records.Remove(key);
            else records[key] = value;
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

        var client = new ManagedMcpClient(
            mcpChatPlugin, _httpClientFactory, _watchdogManager, _loggerFactory);

        await client.StartAsync(cancellationToken);

        _runningMcpClients[mcpChatPlugin.Id] = client;
        mcpChatPlugin.IsRunning = true;

        var tools = await client.ListToolsAsync(cancellationToken);

        var isEnabledRecords = _settings.Plugin.IsEnabledRecords;
        var isPermissionGrantedRecords = _settings.Plugin.IsPermissionGrantedRecords;
        mcpChatPlugin.SetFunctions(
            tools.Select(t => new McpChatFunction(t)
            {
                IsEnabled = !isEnabledRecords.TryGetValue(t.Name, out var isEnabled) || isEnabled, // true if not set
                AutoApprove = isPermissionGrantedRecords.TryGetValue(t.Name, out var isGranted) && isGranted, // false if not set
            }));
    }

    public async Task<IChatPluginScope> CreateScopeAsync(bool isSubagent, CancellationToken cancellationToken)
    {
        var builtInPlugins = _builtInPluginsSource.Items
            .AsValueEnumerable()
            .Where(p => p.IsEnabled && (!isSubagent || p.IsAllowedInSubagent))
            .ToList();

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
        var functionNameDeduplicator = new HashSet<string>();
        return new ChatPluginScope(
            builtInPlugins
                .AsValueEnumerable()
                .Cast<ChatPlugin>()
                .Concat(mcpPlugins)
                .Select(p => new ChatPluginSnapshot(p, functionNameDeduplicator, isSubagent))
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
        public override IDynamicResourceKey HeaderKey => _originalChatPlugin.HeaderKey;
        public override IDynamicResourceKey DescriptionKey => _originalChatPlugin.DescriptionKey;
        public override LucideIconKind? Icon => _originalChatPlugin.Icon;
        public override string? BeautifulIcon => _originalChatPlugin.BeautifulIcon;

        private readonly ChatPlugin _originalChatPlugin;

        public ChatPluginSnapshot(
            ChatPlugin originalChatPlugin,
            HashSet<string> functionNameDeduplicator,
            bool isSubagent) : base(originalChatPlugin.Name)
        {
            _originalChatPlugin = originalChatPlugin;
            AllowedPermissions = originalChatPlugin.AllowedPermissions.ActualValue;
            _functionsSource.AddRange(
                originalChatPlugin
                    .GetEnabledFunctions()
                    .Where(f => !isSubagent || f is not NativeChatFunction { IsAllowedInSubagent: false })
                    .Select(EnsureUniqueFunctionName));

            ChatFunction EnsureUniqueFunctionName(ChatFunction function)
            {
                var metadata = function.KernelFunction.Metadata;
                if (functionNameDeduplicator.Add(metadata.Name)) return function;

                var postfix = 1;
                string newName;
                do
                {
                    newName = $"{metadata.Name}_{postfix++}";
                }
                while (!functionNameDeduplicator.Add(newName));
                metadata.Name = newName;
                return function;
            }
        }
    }

    public void Dispose()
    {
        foreach (var mcpClient in _runningMcpClients.Values)
        {
            mcpClient.DisposeAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }
        _runningMcpClients.Clear();

        _mcpPluginsSource.Edit(items =>
        {
            foreach (var item in items)
            {
                item.IsRunning = false;
            }
        });
        _disposables.Dispose();
    }
}