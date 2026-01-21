namespace Everywhere.StrategyEngine;

/// <summary>
/// Registry for all available strategies.
/// Manages strategy registration, lookup, and lifecycle.
/// </summary>
public interface IStrategyRegistry
{
    /// <summary>
    /// All registered strategies, ordered by priority (descending).
    /// </summary>
    IReadOnlyList<IStrategy> Strategies { get; }

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    /// <param name="strategy">The strategy to register.</param>
    void Register(IStrategy strategy);

    /// <summary>
    /// Registers multiple strategies.
    /// </summary>
    /// <param name="strategies">The strategies to register.</param>
    void RegisterRange(IEnumerable<IStrategy> strategies);

    /// <summary>
    /// Unregisters a strategy by ID.
    /// </summary>
    /// <param name="strategyId">The ID of the strategy to unregister.</param>
    /// <returns>True if the strategy was found and removed, false otherwise.</returns>
    bool Unregister(string strategyId);

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>The strategy, or null if not found.</returns>
    IStrategy? GetById(string strategyId);

    /// <summary>
    /// Reloads strategies from configuration files.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when the strategy list changes.
    /// </summary>
    event EventHandler<StrategyRegistryChangedEventArgs>? StrategiesChanged;
}

/// <summary>
/// Event args for strategy registry changes.
/// </summary>
public sealed class StrategyRegistryChangedEventArgs : EventArgs
{
    public required StrategyRegistryChangeType ChangeType { get; init; }
    public IStrategy? Strategy { get; init; }
}

/// <summary>
/// Types of changes to the strategy registry.
/// </summary>
public enum StrategyRegistryChangeType
{
    Added,
    Removed,
    Reloaded
}
