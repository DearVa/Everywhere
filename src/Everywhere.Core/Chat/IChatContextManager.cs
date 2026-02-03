using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Chat;

/// <summary>
/// Manages chat contexts and their history. This is used behind <see cref="ChatWindowViewModel"/>.
/// </summary>
public interface IChatContextManager : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the current chat context.
    /// </summary>
    ChatContext Current { get; }

    /// <summary>
    /// Gets or sets the current chat context metadata. Setting this will load the corresponding chat context and change Current.
    /// Although this property cannot be null, the Binding may set it to null to indicate no selection.
    /// </summary>
    ChatContextMetadata? CurrentMetadata { get; set; }

    /// <summary>
    /// Gets recent chat context history, grouped by date. 10 ChatContext maximum.
    /// </summary>
    IReadOnlyList<ChatContextHistory> RecentHistory { get; }

    /// <summary>
    /// Command to update recent chat context history.
    /// </summary>
    IRelayCommand UpdateRecentHistoryCommand { get; }

    /// <summary>
    /// Gets all chat context history. 20 for initial load, all loaded on demand.
    /// </summary>
    IReadOnlyList<ChatContextHistory> AllHistory { get; }

    /// <summary>
    /// Command to load more chat context history.
    /// </summary>
    IRelayCommand<int> LoadMoreHistoryCommand { get; }

    /// <summary>
    /// Creates a new chat context and sets it as current.
    /// </summary>
    IRelayCommand CreateNewCommand { get; }

    /// <summary>
    /// Removes the given chat context.
    /// </summary>
    IRelayCommand<ChatContextMetadata> RemoveCommand { get; }

    /// <summary>
    /// Removes the selected chat context from history.
    /// </summary>
    IRelayCommand RemoveSelectedCommand { get; }
    
    /// <summary>
    /// Loads the full chat context for the given metadata.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<ChatContext?> LoadChatContextAsync(ChatContextMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get and ensures the working directory for the given chat context.
    /// </summary>
    /// <returns>
    /// Usually a temporary directory path like C:\Users\[UserName]\AppData\Roaming\Everywhere\plugins\2025-12-30
    /// </returns>
    string EnsureWorkingDirectory(ChatContext chatContext);

    /// <summary>
    /// Populates the system prompt for the given chat context.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="systemPrompt"></param>
    string RenderSystemPrompt(ChatContext chatContext, string? systemPrompt);
}