using System.Diagnostics;
using System.Net;
using System.Text;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ZLinq;

namespace Everywhere.Chat.Plugins.Mcp;

/// <summary>
/// Manages the lifecycle of an <see cref="McpClient"/>, including creation, reconnection on session expiry, and disposal.
/// Encapsulates transport creation logic (Stdio / HTTP) and watchdog registration.
/// </summary>
internal sealed class ManagedMcpClient(
    McpChatPlugin mcpChatPlugin,
    IHttpClientFactory httpClientFactory,
    IWatchdogManager watchdogManager,
    ILoggerFactory loggerFactory
) : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the current session has been marked as expired.
    /// </summary>
    public bool IsSessionExpired { get; private set; }

    private readonly ILogger _logger = loggerFactory.CreateLogger<ManagedMcpClient>();
    private readonly McpLoggerFactory _mcpLoggerFactory = new(mcpChatPlugin, loggerFactory);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    private McpClient? _mcpClient;
    private IClientTransport? _clientTransport;
    private List<ManagedMcpClientTool>? _tools;

    /// <summary>
    /// Starts the MCP client by creating a transport and connecting.
    /// Sets up process watchdog for stdio transports.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var transportConfiguration = mcpChatPlugin.TransportConfiguration
            ?? throw new InvalidOperationException("MCP transport configuration is not set.");

        _clientTransport = CreateTransport(transportConfiguration);

        _mcpClient = await McpClient.CreateAsync(
            _clientTransport,
            null,
            _mcpLoggerFactory,
            cancellationToken);

        IsSessionExpired = false;

        await RegisterStdioWatchdogAsync(_clientTransport);
        MonitorCompletion();
    }

    /// <summary>
    /// Lists tools from the MCP client, wrapping them in <see cref="ManagedMcpClientTool"/> with escaped names.
    /// </summary>
    public async Task<IList<ManagedMcpClientTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ListToolsCoreAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsSessionExpiredError(ex))
        {
            _logger.LogWarning(ex, "Session expired during ListTools, reconnecting...");
            await ReconnectAsync(cancellationToken);
            return await ListToolsCoreAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reconnects by disposing the old client and creating a new one.
    /// Thread-safe via semaphore.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        await _reconnectLock.WaitAsync(cancellationToken);
        try
        {
            // Only HTTP transports should attempt reconnection.
            if (mcpChatPlugin.TransportConfiguration is not HttpMcpTransportConfiguration)
            {
                throw new InvalidOperationException("Reconnection is only supported for HTTP MCP transports.");
            }

            _logger.LogInformation("Reconnecting MCP client for plugin {PluginName}...", mcpChatPlugin.Name);

            // Dispose old client.
            if (_mcpClient is not null)
            {
                await _mcpClient.DisposeAsync();
                _mcpClient = null;
            }

            var transportConfiguration = mcpChatPlugin.TransportConfiguration
                ?? throw new InvalidOperationException("MCP transport configuration is not set.");

            _clientTransport = CreateTransport(transportConfiguration);

            _mcpClient = await McpClient.CreateAsync(
                _clientTransport,
                null,
                _mcpLoggerFactory,
                cancellationToken);

            IsSessionExpired = false;
            MonitorCompletion();

            // Re-list tools and update existing ManagedMcpClientTool references.
            if (_tools is { Count: > 0 })
            {
                var newTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
                foreach (var managedTool in _tools)
                {
                    var newTool = newTools.AsValueEnumerable()
                        .FirstOrDefault(t => t.ProtocolTool.Name == managedTool.ProtocolToolName);
                    if (newTool is not null)
                    {
                        managedTool.UpdateInnerTool(newTool);
                    }
                }
            }

            _logger.LogInformation("MCP client reconnected for plugin {PluginName}.", mcpChatPlugin.Name);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    /// <summary>
    /// Determines whether the given exception indicates an MCP session expired error.
    /// </summary>
    internal bool IsSessionExpiredError(Exception ex)
    {
        // Only attempt reconnection for HTTP transports.
        if (mcpChatPlugin.TransportConfiguration is not HttpMcpTransportConfiguration)
            return false;

        if (CheckCompletionForSessionExpiry())
            return true;

        return false;
    }

    /// <summary>
    /// Synchronously checks the <see cref="McpClient.Completion"/> task to determine
    /// if the session has expired (HTTP 404 from the MCP server).
    /// </summary>
    private bool CheckCompletionForSessionExpiry()
    {
        if (IsSessionExpired)
            return true;

        if (_mcpClient?.Completion is
            {
                IsCompletedSuccessfully: true,
                Result: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound }
            })
        {
            IsSessionExpired = true;
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
            _mcpClient = null;
        }

        _reconnectLock.Dispose();
    }

    private async Task<IList<ManagedMcpClientTool>> ListToolsCoreAsync(CancellationToken cancellationToken)
    {
        if (_mcpClient is null) throw new InvalidOperationException("MCP client is not started.");

        var rawTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);

        // Escape names and deduplicate.
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var managedTools = new List<ManagedMcpClientTool>(rawTools.Count);

        foreach (var tool in rawTools)
        {
            var escapedName = EscapeToolName(tool.Name);

            if (nameCount.TryGetValue(escapedName, out var count))
            {
                nameCount[escapedName] = count + 1;
                escapedName = $"{escapedName}_{count}";
            }
            else
            {
                nameCount[escapedName] = 1;
            }

            managedTools.Add(new ManagedMcpClientTool(tool, this, escapedName));
        }

        _tools = managedTools;
        return managedTools;
    }

    /// <summary>
    /// MCP tool names allow A-Z a-z 0-9 _ - .
    /// but SK/AIFunction only allows A-Z a-z 0-9 _
    /// so we replace - and . with _
    /// </summary>
    private static string EscapeToolName(string name)
    {
        if (name.AsSpan().IndexOfAny('-', '.') < 0)
            return name;

        return new string(name.AsValueEnumerable().Select(static c => c is '-' or '.' ? '_' : c).ToArray());
    }

    private IClientTransport CreateTransport(McpTransportConfiguration transportConfiguration)
    {
        return transportConfiguration switch
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
                _mcpLoggerFactory),
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
                httpClientFactory.CreateClient(McpServiceExtension.McpClientName),
                _mcpLoggerFactory),
            _ => throw new InvalidOperationException("Unsupported MCP transport configuration type.")
        };
    }

    private async Task RegisterStdioWatchdogAsync(IClientTransport clientTransport)
    {
        if (_mcpClient is null || clientTransport is not StdioClientTransport) return;

        var processId = -1;
        try
        {
            var transport = GetMcpClientTransport(_mcpClient);
            if (GetStdioClientSessionTransportProcess(transport) is { HasExited: false, Id: > 0 } process)
            {
                process.Exited += HandleProcessExited;

                void HandleProcessExited(object? sender, EventArgs e)
                {
                    mcpChatPlugin.IsRunning = false;
                    process.Exited -= HandleProcessExited;
                }

                await watchdogManager.RegisterProcessAsync(process.Id);
                processId = process.Id;
            }
        }
        finally
        {
            if (processId == -1 && mcpChatPlugin.TransportConfiguration is StdioMcpTransportConfiguration stdio)
            {
                _logger.LogWarning(
                    "MCP started with stdio transport, but failed to get the underlying process ID for watchdog registration. " +
                    "Command: {Command}, Arguments: {Arguments}",
                    stdio.Command,
                    stdio.Arguments);
            }
        }
    }

    private void MonitorCompletion()
    {
        _mcpClient?.Completion.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    var details = task.Result;
                    if (task.Result is HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound })
                    {
                        IsSessionExpired = true;
                        _logger.LogWarning("MCP session expired for plugin {PluginName}.", mcpChatPlugin.Name);
                    }
                    else if (details.Exception is not null)
                    {
                        _logger.LogWarning(details.Exception, "MCP client completed with error for plugin {PluginName}.", mcpChatPlugin.Name);
                    }

                    return;
                }

                if (task.IsFaulted)
                {
                    _logger.LogWarning(task.Exception, "MCP client completion task faulted for plugin {PluginName}.", mcpChatPlugin.Name);
                }
                else if (task.IsCanceled)
                {
                    _logger.LogWarning("MCP client completion task was canceled for plugin {PluginName}.", mcpChatPlugin.Name);
                }
            },
            TaskContinuationOptions.ExecuteSynchronously).Detach(_logger.ToExceptionHandler());
    }

    private string EnsureWorkingDirectory(string? workingDirectory)
    {
        if (Directory.Exists(workingDirectory)) return workingDirectory;

        var fallbackDir = RuntimeConstants.EnsureWritableDataFolderPath("plugins", "mcp", mcpChatPlugin.Id.ToString("N"));
        return fallbackDir;
    }

    private static Dictionary<string, string?> EnsureLatestPath(Dictionary<string, string?> environmentVariables)
    {
        var latestPath = EnvironmentVariableUtilities.GetLatestPathVariable();
        if (latestPath.IsNullOrEmpty()) return environmentVariables;

        var pathBuilder = new StringBuilder(latestPath);

        if (environmentVariables.TryGetValue("PATH", out var existingPath) && !existingPath.IsNullOrEmpty())
        {
            pathBuilder.Append(Path.PathSeparator).Append(existingPath);
        }

        environmentVariables["PATH"] = pathBuilder.ToString();
        return environmentVariables;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_transport")]
    private static extern ref ITransport GetMcpClientTransport(
        [UnsafeAccessorType("ModelContextProtocol.Client.McpClientImpl, ModelContextProtocol.Core")]
        object client);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_process")]
    private static extern ref Process GetStdioClientSessionTransportProcess(
        [UnsafeAccessorType("ModelContextProtocol.Client.StdioClientSessionTransport, ModelContextProtocol.Core")]
        object transport);

    /// <summary>
    /// Used to create ILogger instances for MCP clients.
    /// Logs to both the Everywhere logging system and the <see cref="McpChatPlugin"/>'s log entries.
    /// </summary>
    private sealed class McpLoggerFactory(McpChatPlugin mcpChatPlugin, ILoggerFactory innerLoggerFactory) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) => innerLoggerFactory.AddProvider(provider);

        public ILogger CreateLogger(string categoryName)
        {
            var innerLogger = innerLoggerFactory.CreateLogger(categoryName);
            return new McpLogger(mcpChatPlugin, innerLogger);
        }

        public void Dispose() => innerLoggerFactory.Dispose();

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