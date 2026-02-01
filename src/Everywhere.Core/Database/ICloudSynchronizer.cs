using Everywhere.Common;

namespace Everywhere.Database;

/// <summary>
/// Defines methods for synchronizing chat database with a remote source.
/// </summary>
public interface IChatDbSynchronizer : IAsyncInitializer
{
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}