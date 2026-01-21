using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Context for executing a strategy.
/// </summary>
public record StrategyExecutionContext
{
    // public required CustomAssistant CustomAssistant { get; init; }

    public required IChatPluginScope ChatPluginScope { get; init; }

    public string? SystemPrompt { get; init; }

    public required UserChatMessage UserChatMessage { get; init; }
}