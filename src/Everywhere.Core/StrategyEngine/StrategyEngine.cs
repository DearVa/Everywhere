using Serilog;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of <see cref="IStrategyEngine"/>.
/// Orchestrates strategy matching and command generation.
/// </summary>
public sealed class StrategyEngine(IStrategyRegistry registry) : IStrategyEngine
{
    private static readonly ILogger Logger = Log.ForContext<StrategyEngine>();

    public IStrategyRegistry Registry { get; } = registry;

    public IReadOnlyList<StrategyCommand> GetCommands(
        StrategyContext context,
        int maxCommands = 10)
    {
        ArgumentNullException.ThrowIfNull(context);

        var matchedStrategies = new List<IStrategy>();
        var allCommands = new List<StrategyCommand>();

        // Evaluate all strategies
        foreach (var strategy in Registry.Strategies)
        {
            try
            {
                if (!strategy.Matches(context)) continue;

                matchedStrategies.Add(strategy);

                var commands = strategy.GetCommands(context);
                allCommands.AddRange(commands);

#if DEBUG
                Logger.Debug(
                    "Strategy {StrategyId} matched, generated {CommandCount} commands",
                    strategy.Id,
                    commands.Count());
#endif
            }
            catch (Exception ex)
            {
                Logger.Warning(ex,
                    "Error evaluating strategy {StrategyId}",
                    strategy.Id);
            }
        }

        // Merge and deduplicate commands
        var mergedCommands = MergeCommands(allCommands, maxCommands);

        Logger.Information(
            "Generated {CommandCount} commands from {StrategyCount} matching strategies",
            mergedCommands.Count,
            matchedStrategies.Count);

        return mergedCommands;
    }

    public Task<StrategyExecutionContext> CreateExecutionContextAsync(
        StrategyCommand command,
        StrategyContext context,
        CancellationToken cancellationToken = default)
    {
        // TODO: Implement command execution in Phase 2
        // This will:
        // 1. Resolve template variables
        // 2. Configure allowed tools
        // 3. Start an agent session with the configured prompt

        throw new NotImplementedException();
    }

    /// <summary>
    /// Merges commands from multiple strategies.
    /// Deduplicates by ID (keeps highest priority) and sorts by priority.
    /// </summary>
    private static List<StrategyCommand> MergeCommands(
        List<StrategyCommand> commands,
        int maxCommands)
    {
        if (commands.Count == 0)
        {
            return [];
        }

        // Group by ID, keep highest priority version
        var deduped = commands
            .GroupBy(c => c.Id)
            .Select(g => g.OrderByDescending(c => c.Priority).First());

        // Sort by priority and take top N
        var result = deduped
            .OrderByDescending(c => c.Priority)
            .Take(maxCommands)
            .ToList();

        return result;
    }
}
