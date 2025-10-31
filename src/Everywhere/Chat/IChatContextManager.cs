﻿using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Chat;

public interface IChatContextManager : INotifyPropertyChanged
{
    /// <summary>
    /// Gets chat message nodes on the current branch.
    /// </summary>
    IReadOnlyList<ChatMessageNode> ChatMessageNodes { get; }

    /// <summary>
    /// Gets the current chat context.
    /// </summary>
    ChatContext Current { get; }

    /// <summary>
    /// Gets or sets the current chat context metadata. Setting this will load the corresponding chat context and change Current.
    /// </summary>
    ChatContextMetadata CurrentMetadata { get; set; }

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
    /// Gets system prompt variables that can be used in system prompts.
    /// </summary>
    IReadOnlyDictionary<string, Func<string>> SystemPromptVariables { get; }

    /// <summary>
    /// Creates a new chat context and sets it as current.
    /// </summary>
    IRelayCommand CreateNewCommand { get; }

    /// <summary>
    /// Removes the given chat context.
    /// </summary>
    IRelayCommand<ChatContextMetadata> RemoveCommand { get; }
}