using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.StrategyEngine;

/// <summary>
/// A strategy defines conditions for matching contexts and produces commands.
/// Strategies are the core building blocks of the Strategy Engine.
/// </summary>
public interface IStrategy
{
    /// <summary>
    /// Unique identifier for this strategy.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name.
    /// </summary>
    DynamicResourceKeyBase Name { get; }

    /// <summary>
    /// Optional description of what this strategy does.
    /// </summary>
    DynamicResourceKeyBase? Description { get; }

    /// <summary>
    /// Priority for conflict resolution when multiple strategies match.
    /// Higher priority strategies' commands are displayed first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Whether this strategy is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Evaluates if this strategy matches the given context.
    /// </summary>
    /// <param name="context">The strategy context to evaluate.</param>
    /// <returns>True if the strategy matches, false otherwise.</returns>
    bool Matches(StrategyContext context);

    /// <summary>
    /// Generates commands for the matched context.
    /// Should only be called after <see cref="Matches"/> returns true.
    /// </summary>
    /// <param name="context">The strategy context.</param>
    /// <returns>Commands to display to the user.</returns>
    IEnumerable<StrategyCommand> GetCommands(StrategyContext context);
}

/// <summary>
/// Base class for implementing strategies with common functionality.
/// </summary>
public abstract class StrategyBase : ObservableObject, IStrategy
{
    public abstract string Id { get; }
    public abstract DynamicResourceKeyBase Name { get; }
    public virtual DynamicResourceKeyBase? Description => null;
    public virtual int Priority => 0;
    public virtual bool IsEnabled => true;

    /// <summary>
    /// The conditions that must be satisfied for this strategy to match.
    /// Override to provide custom condition logic.
    /// </summary>
    protected virtual IStrategyCondition? Condition => null;

    public virtual bool Matches(StrategyContext context)
    {
        return Condition?.Evaluate(context) ?? true;
    }

    public abstract IEnumerable<StrategyCommand> GetCommands(StrategyContext context);

    /// <summary>
    /// Helper to create a command with this strategy as the source.
    /// </summary>
    protected StrategyCommand CreateCommand(
        string id,
        string name,
        string? userMessage = null,
        string? systemPrompt = null,
        int priority = 0)
    {
        return new StrategyCommand
        {
            Id = id,
            Name = name,
            UserMessage = userMessage,
            SystemPrompt = systemPrompt,
            Priority = priority,
            SourceStrategyId = Id
        };
    }
}
