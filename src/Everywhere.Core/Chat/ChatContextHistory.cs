using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Chat;

/// <summary>
/// Presents one stable humanized-date group from the incremental chat history result.
/// </summary>
public sealed class ChatContextHistory : IDisposable
{
    /// <summary>
    /// Gets the humanized date represented by this group.
    /// </summary>
    public HumanizedDate Date { get; }

    /// <summary>
    /// Gets the group's metadata ordered from most recently modified to least recently modified.
    /// </summary>
    public IReadOnlyBindableList<ChatContextMetadata> MetadataList { get; }

    private readonly IDisposable _metadataConnection;

    internal ChatContextHistory(IGroup<ChatContextMetadata, HumanizedDate> group)
    {
        Date = group.GroupKey;
        MetadataList = group.List
            .Connect()
            .Sort(
                SortExpressionComparer<ChatContextMetadata>
                    .Descending(static metadata => metadata.DateModified)
                    .ThenByDescending(static metadata => metadata.Id))
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _metadataConnection);
    }

    /// <summary>
    /// Releases the DynamicData binding owned by this presentation group.
    /// </summary>
    public void Dispose() => _metadataConnection.Dispose();
}