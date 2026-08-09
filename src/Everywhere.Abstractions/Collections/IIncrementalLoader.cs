using System.ComponentModel;

namespace Everywhere.Collections;

/// <summary>
/// Represents a data source that materializes an ordered result set in bounded increments.
/// </summary>
/// <remarks>
/// The loader owns filtering and source traversal. A requested count therefore refers to items
/// added to the final result, not to raw records inspected while producing that result.
/// </remarks>
public interface IIncrementalLoader : INotifyPropertyChanged
{
    /// <summary>
    /// Gets whether another load can potentially add items to the result set.
    /// </summary>
    bool HasMoreItems { get; }

    /// <summary>
    /// Gets whether one or more incremental loading sessions are active.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Begins a continuous loading session, such as filling one viewport or responding to one
    /// scroll-to-end gesture.
    /// </summary>
    IIncrementalLoadSession BeginLoadSession();
}

/// <summary>
/// Keeps the lifetime of a continuous incremental loading operation explicit.
/// </summary>
public interface IIncrementalLoadSession : IDisposable
{
    /// <summary>
    /// Attempts to append up to <paramref name="count"/> items to the final result set.
    /// </summary>
    /// <param name="count">The desired number of final result items.</param>
    /// <param name="cancellationToken">Cancels the caller's loading operation.</param>
    /// <returns>The number of items added and whether more items may still be available.</returns>
    ValueTask<IncrementalLoadResult> LoadMoreAsync(int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the observable result of one incremental load request.
/// </summary>
/// <param name="AddedItemCount">The number of final result items appended by the request.</param>
/// <param name="HasMoreItems">Whether a later request can potentially append more items.</param>
public readonly record struct IncrementalLoadResult(int AddedItemCount, bool HasMoreItems);