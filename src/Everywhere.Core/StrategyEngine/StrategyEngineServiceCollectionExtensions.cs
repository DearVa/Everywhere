using Everywhere.StrategyEngine.BuiltIn;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Extension methods for registering Strategy Engine services.
/// </summary>
public static class StrategyEngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Strategy Engine services to the service collection.
    /// </summary>
    public static IServiceCollection AddStrategyEngine(this IServiceCollection services)
    {
        // Register core services
        services.AddSingleton<IStrategyRegistry, StrategyRegistry>();
        services.AddSingleton<IStrategyEngine, StrategyEngine>();

        // Register built-in strategies
        services.AddSingleton<IStrategy, GlobalStrategy>();
        services.AddSingleton<IStrategy, BrowserStrategy>();
        services.AddSingleton<IStrategy, CodeEditorStrategy>();
        services.AddSingleton<IStrategy, TextSelectionStrategy>();
        services.AddSingleton<IStrategy, FileStrategy>();

        return services;
    }
}
