namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of <see cref="IStrategyRegistry"/>.
/// Manages strategy registration and provides ordered access.
/// </summary>
public sealed class StrategyRegistry : IStrategyRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IStrategy> _strategiesById = new();
    private List<IStrategy>? _orderedStrategies;

    public IReadOnlyList<IStrategy> Strategies
    {
        get
        {
            lock (_lock)
            {
                _orderedStrategies ??= _strategiesById.Values
                    .Where(s => s.IsEnabled)
                    .OrderByDescending(s => s.Priority)
                    .ToList();

                return _orderedStrategies;
            }
        }
    }

    public void Register(IStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        lock (_lock)
        {
            _strategiesById[strategy.Id] = strategy;
            _orderedStrategies = null; // Invalidate cache
        }

        OnStrategiesChanged(new StrategyRegistryChangedEventArgs
        {
            ChangeType = StrategyRegistryChangeType.Added,
            Strategy = strategy
        });
    }

    public void RegisterRange(IEnumerable<IStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        lock (_lock)
        {
            foreach (var strategy in strategies)
            {
                _strategiesById[strategy.Id] = strategy;
            }

            _orderedStrategies = null; // Invalidate cache
        }

        OnStrategiesChanged(new StrategyRegistryChangedEventArgs
        {
            ChangeType = StrategyRegistryChangeType.Reloaded
        });
    }

    public bool Unregister(string strategyId)
    {
        ArgumentNullException.ThrowIfNull(strategyId);

        IStrategy? removed;
        lock (_lock)
        {
            if (!_strategiesById.Remove(strategyId, out removed))
            {
                return false;
            }

            _orderedStrategies = null; // Invalidate cache
        }

        OnStrategiesChanged(new StrategyRegistryChangedEventArgs
        {
            ChangeType = StrategyRegistryChangeType.Removed,
            Strategy = removed
        });

        return true;
    }

    public IStrategy? GetById(string strategyId)
    {
        ArgumentNullException.ThrowIfNull(strategyId);

        lock (_lock)
        {
            return _strategiesById.GetValueOrDefault(strategyId);
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        // TODO: Implement YAML configuration loading in Phase 2
        // For now, this is a no-op as strategies are registered programmatically

        OnStrategiesChanged(new StrategyRegistryChangedEventArgs
        {
            ChangeType = StrategyRegistryChangeType.Reloaded
        });

        return Task.CompletedTask;
    }

    public event EventHandler<StrategyRegistryChangedEventArgs>? StrategiesChanged;

    private void OnStrategiesChanged(StrategyRegistryChangedEventArgs args)
    {
        StrategiesChanged?.Invoke(this, args);
    }

    /// <summary>
    /// Gets all strategies including disabled ones.
    /// </summary>
    public IReadOnlyList<IStrategy> AllStrategies
    {
        get
        {
            lock (_lock)
            {
                return _strategiesById.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Clears all registered strategies.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _strategiesById.Clear();
            _orderedStrategies = null;
        }

        OnStrategiesChanged(new StrategyRegistryChangedEventArgs
        {
            ChangeType = StrategyRegistryChangeType.Reloaded
        });
    }
}
