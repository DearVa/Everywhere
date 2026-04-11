using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Everywhere.Chat.Plugins.Mcp;

/// <summary>
/// An <see cref="AIFunction"/> wrapper around <see cref="McpClientTool"/> that intercepts invocations,
/// detects session expiry errors, and silently reconnects and retries.
/// </summary>
public sealed class ManagedMcpClientTool : AIFunction
{
    /// <summary>
    /// The original (unescaped) protocol tool name for matching after reconnection.
    /// </summary>
    public string ProtocolToolName => _innerTool.ProtocolTool.Name;

    public override string Name { get; }

    public override string Description => _innerTool.Description;

    public override JsonElement JsonSchema => _innerTool.JsonSchema;

    public override JsonElement? ReturnJsonSchema => _innerTool.ReturnJsonSchema;

    public override JsonSerializerOptions JsonSerializerOptions => _innerTool.JsonSerializerOptions;

    private readonly ManagedMcpClient _managedClient;
    private McpClientTool _innerTool;

    internal ManagedMcpClientTool(McpClientTool innerTool, ManagedMcpClient managedClient, string escapedName)
    {
        _innerTool = innerTool;
        _managedClient = managedClient;
        Name = escapedName;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _innerTool.InvokeAsync(arguments, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && _managedClient.IsSessionExpiredError(ex))
        {
            // Reconnect and retry once.
            await _managedClient.ReconnectAsync(cancellationToken);
            return await _innerTool.InvokeAsync(arguments, cancellationToken);
        }
    }

    /// <summary>
    /// Updates the inner <see cref="McpClientTool"/> reference after a reconnection.
    /// Called by <see cref="ManagedMcpClient.ReconnectAsync"/> when new tools are obtained.
    /// </summary>
    internal void UpdateInnerTool(McpClientTool newTool)
    {
        _innerTool = newTool;
    }
}
