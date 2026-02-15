using Everywhere.AI;

namespace Everywhere.Cloud;

/// <summary>
/// Provides official model definitions for the Everywhere AI platform.
/// </summary>
public interface IOfficialModelProvider
{
    /// <summary>
    /// This should be an observable collection that notifies subscribers when the list of model definitions changes.
    /// This should refresh before & after get is called.
    /// </summary>
    IReadOnlyList<ModelDefinitionTemplate> ModelDefinitions { get; }

    /// <summary>
    /// Manually refresh the list of model definitions from the official source.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RefreshModelDefinitionsAsync(CancellationToken cancellationToken = default);
}