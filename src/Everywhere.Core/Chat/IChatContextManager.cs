using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;

namespace Everywhere.Chat;

/// <summary>
/// Owns the current chat context and the incrementally materialized chat-history presentation.
/// </summary>
public interface IChatContextManager : IIncrementalLoader
{
    /// <summary>
    /// Gets the current chat context.
    /// </summary>
    ChatContext Current { get; }

    /// <summary>
    /// Gets or sets the current chat context metadata. Setting this loads the corresponding chat
    /// context. A binding may temporarily assign null to indicate no selection.
    /// </summary>
    ChatContextMetadata? CurrentMetadata { get; set; }

    /// <summary>
    /// Gets the command that invalidates cached paging state and reloads history from the newest row.
    /// </summary>
    IRelayCommand UpdateRecentHistoryCommand { get; }

    /// <summary>
    /// Gets the stable, dynamically grouped portion of history materialized so far.
    /// </summary>
    IReadOnlyBindableList<ChatContextHistory> AllHistory { get; }

    /// <summary>
    /// Gets or sets the text used to filter incrementally materialized history.
    /// </summary>
    string? HistorySearchQuery { get; set; }

    /// <summary>
    /// Gets or sets whether title misses may inspect user and assistant text content. Tool calls and
    /// tool results never participate in this search.
    /// </summary>
    bool HistorySearchIncludesContent { get; set; }

    /// <summary>
    /// Gets the number of running chat contexts in the background.
    /// </summary>
    int BackgroundBusyCount { get; }

    /// <summary>
    /// Gets the number of unacknowledged notifications from background chat contexts.
    /// </summary>
    int BackgroundNotificationCount { get; }

    /// <summary>
    /// Gets the command that creates and activates a new chat context.
    /// </summary>
    IRelayCommand CreateNewCommand { get; }

    /// <summary>
    /// Gets the command that removes a chat context.
    /// </summary>
    IRelayCommand<ChatContextMetadata> RemoveCommand { get; }

    /// <summary>
    /// Loads the full chat context for the given metadata.
    /// </summary>
    Task<ChatContext?> LoadChatContextAsync(ChatContextMetadata metadata, CancellationToken cancellationToken = default);
}