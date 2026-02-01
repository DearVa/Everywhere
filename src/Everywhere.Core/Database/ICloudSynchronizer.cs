using Everywhere.Common;

namespace Everywhere.Database;

/// <summary>
/// Defines methods for synchronizing chat database with a remote source.
/// </summary>
public interface IChatDbSynchronizer : IAsyncInitializer
{
    /// <summary>
    /// Manually triggers synchronization with the remote source.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}