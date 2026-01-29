using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Storage;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;
using ZLinq;

namespace Everywhere.Chat;

public partial class ChatContextManager : ObservableObject, IChatContextManager, IAsyncInitializer, IRecipient<ChatContextMetadataChangedMessage>
{
    public ChatContext Current
    {
        get
        {
            if (_current is not null) return _current;

            CreateNew();
            return _current;
        }
    }

    public ChatContextMetadata? CurrentMetadata
    {
        get => Current.Metadata;
        set
        {
            if (value is null) return;

            if (value.Id == Guid.Empty)
                throw new ArgumentException("The provided chat context does not have a valid ID.", nameof(value));

            if (!_metadataMap.ContainsKey(value.Id))
                throw new ArgumentException("The provided chat context is not part of the history.", nameof(value));

            var previous = _current;
            if (previous?.Metadata.Id == value.Id) return;
            OnPropertyChanged();

            // Update active state
            previous?.VisualElements.IsActive = false;

            Task.Run(async () =>
            {
                _current = await LoadChatContextAsync(value.Id, false, CancellationToken.None);
                if (_current is null)
                {
                    CreateNew();
                }
                else
                {
                    NotifyCurrentChanged();
                }

                _current.VisualElements.IsActive = true;

                // WARNING:
                // IDK why if I remove the previous context immediately,
                // Avalonia will fuck up and crash immediately with IndexOutOfRangeException.
                // The whole call stack is inside Avalonia, so I can't do anything about it.
                // The only workaround is to invoke the removal on the UI thread with a delay.
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        CreateNewCommand.NotifyCanExecuteChanged();

                        if (IsEmptyContext(previous) || previous?.Metadata.IsTemporary is true)
                        {
                            // Remove empty or temporary chat
                            if (_metadataMap.Remove(previous.Metadata.Id))
                            {
                                OnPropertyChanged(nameof(AllHistory));
                                OnPropertyChanged(nameof(RecentHistory));
                            }
                        }

                        RemoveCommand.NotifyCanExecuteChanged();
                    },
                    DispatcherPriority.Background);
            });
        }
    }

    public IReadOnlyList<ChatContextHistory> RecentHistory => ApplyHistory(_recentHistory, 9);

    IRelayCommand IChatContextManager.UpdateRecentHistoryCommand => UpdateRecentHistoryCommand;

    public IReadOnlyList<ChatContextHistory> AllHistory => ApplyHistory(_allHistory, int.MaxValue);

    IRelayCommand<int> IChatContextManager.LoadMoreHistoryCommand => LoadMoreHistoryCommand;

    [field: AllowNull, MaybeNull]
    public IRelayCommand CreateNewCommand => field ??= new RelayCommand(CreateNew, () => !IsEmptyContext(_current));

    IRelayCommand<ChatContextMetadata> IChatContextManager.RemoveCommand => RemoveCommand;

    IRelayCommand IChatContextManager.RemoveSelectedCommand => RemoveSelectedCommand;

    private ICollection<ChatContextMetadata> LoadedMetadata => _metadataMap.Values;

    private ChatContext? _current;

    private readonly Dictionary<Guid, ChatContextMetadata> _metadataMap = [];
    private readonly ObservableCollection<ChatContextHistory> _recentHistory = [];
    private readonly ObservableCollection<ChatContextHistory> _allHistory = [];

    /// <summary>
    /// A buffer for chat contexts and their metadata to be saved.
    /// Sometimes only metadata needs to be saved (e.g., when only the topic is changed), in which case the context can be null.
    /// </summary>
    private readonly Dictionary<Guid, ChatContextMetadataChangedMessage> _saveBuffer = [];

    private readonly Settings _settings;
    private readonly IChatContextStorage _chatContextStorage;
    private readonly IRuntimeConstantProvider _runtimeConstantProvider;
    private readonly ILogger<ChatContextManager> _logger;
    private readonly DebounceExecutor<ChatContextManager, ThreadingTimerImpl> _saveDebounceExecutor;

    public ChatContextManager(
        Settings settings,
        IChatContextStorage chatContextStorage,
        IRuntimeConstantProvider runtimeConstantProvider,
        ILogger<ChatContextManager> logger)
    {
        _settings = settings;
        _chatContextStorage = chatContextStorage;
        _runtimeConstantProvider = runtimeConstantProvider;
        _logger = logger;
        _saveDebounceExecutor = new DebounceExecutor<ChatContextManager, ThreadingTimerImpl>(
            () => this,
            static that =>
            {
                List<ChatContextMetadataChangedMessage> toSave;
                lock (that._saveBuffer)
                {
                    toSave = that._saveBuffer.Values.ToList(); // ToList is better than ToArray (less allocation)
                    that._saveBuffer.Clear();
                }
                Task.WhenAll(
                        toSave.AsValueEnumerable()
                            .Where(p => !IsEmptyContext(p.Context) && !p.Metadata.IsTemporary)
                            .Select(p => p.Context is not null ?
                                that._chatContextStorage.SaveChatContextAsync(p.Context) :
                                that._chatContextStorage.SaveChatContextMetadataAsync(p.Metadata)) // only save metadata if context is null
                            .ToList())
                    .Detach(that._logger.ToExceptionHandler());
            },
            TimeSpan.FromSeconds(0.5)
        );

        WeakReferenceMessenger.Default.Register(this);

        Task.Run(CleanupUnusedWorkingDirectories);
    }

    /// <summary>
    /// Handles chat context changed events.
    /// </summary>
    /// <param name="message"></param>
    public void Receive(ChatContextMetadataChangedMessage message)
    {
        if (message.PropertyName is not nameof(ChatContextMetadata.DateModified) and not nameof(ChatContextMetadata.Topic))
            return; // Only care about these two properties

        lock (_saveBuffer)
        {
            ref var valueRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_saveBuffer, message.Metadata.Id, out _);
            if (valueRef is null) valueRef = message;
            else
            {
                valueRef.Context ??= message.Context;
                valueRef.Metadata = message.Metadata;
            }
        }
        _saveDebounceExecutor.Trigger();

        Dispatcher.UIThread.InvokeOnDemand(CreateNewCommand.NotifyCanExecuteChanged);
    }

    /// <summary>
    /// Delete all directories in _runtimeConstantProvider.EnsureWritableDataFolderPath($"plugins") that named with date (yyyy-MM-dd)
    /// </summary>
    private void CleanupUnusedWorkingDirectories()
    {
        var regex = WorkingDirectoryRegex();
        var pluginsDir = _runtimeConstantProvider.EnsureWritableDataFolderPath("plugins");
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!regex.IsMatch(dirName)) continue;

            if (!DateTime.TryParseExact(dirName, "yyyy-MM-dd", null, DateTimeStyles.None, out var dirDate))
                continue;

            // If the directory is 3 days later and is empty, delete it
            if ((DateTime.Now - dirDate).TotalDays > 3 && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try
                {
                    Directory.Delete(dir); // do not use recursive delete
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unused working directory: {Directory}", dir);
                }
            }
        }
    }

    [RelayCommand]
    private async Task UpdateRecentHistoryAsync()
    {
        try
        {
            _metadataMap.Clear();
            if (_current is not null)
            {
                _metadataMap[_current.Metadata.Id] = _current.Metadata;
            }

            await LoadMetadataAsync(9, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLocator.Resolve<ToastManager>().CreateToast(LocaleResolver.Common_Error).WithContent(ex.GetFriendlyMessage()).ShowError();
            _logger.LogError(ex, "Failed to update recent chat context history");
        }
    }

    [RelayCommand]
    private async Task LoadMoreHistoryAsync(int count)
    {
        try
        {
            var lastId = LoadedMetadata
                .AsValueEnumerable()
                .OrderByDescending(c => c.DateModified)
                .Select(c => c.Id)
                .LastOrDefault();
            await LoadMetadataAsync(count, lastId == Guid.Empty ? null : lastId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceLocator.Resolve<ToastManager>().CreateToast(LocaleResolver.Common_Error).WithContent(ex.GetFriendlyMessage()).ShowError();
            _logger.LogError(ex, "Failed to load more chat context history");
        }
    }

    [MemberNotNull(nameof(_current))]
    private void CreateNew()
    {
        if (IsEmptyContext(_current)) return;

        var isCurrentTemporary = _current?.Metadata.IsTemporary is true;
        if (isCurrentTemporary)
        {
            // Remove the temporary chat context before creating a new one
            // Temporary chat contexts are not saved to storage, so no need to delete from storage.
            _metadataMap.Remove(_current!.Metadata.Id);
        }

        _current = new ChatContext
        {
            Metadata =
            {
                IsTemporary = _settings.ChatWindow.TemporaryChatMode switch
                {
                    TemporaryChatMode.RememberLast => isCurrentTemporary,
                    TemporaryChatMode.Always => true,
                    _ => false
                },
            },
        };

        _metadataMap.Add(_current.Metadata.Id, _current.Metadata);
        // After created, the chat context is not added to the storage yet.
        // It will be added when it's property has changed.

        OnPropertyChanged(nameof(AllHistory));
        OnPropertyChanged(nameof(RecentHistory));
        NotifyCurrentChanged();
    }

    private bool CanRemove => _metadataMap.Count > 1 || !IsEmptyContext(_current);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(ChatContextMetadata metadata, CancellationToken cancellationToken)
    {
        if (!_metadataMap.Remove(metadata.Id)) return;

        // delete in background
        Task.Run(() => _chatContextStorage.DeleteChatContextAsync(metadata.Id, cancellationToken), cancellationToken)
            .Detach(_logger.ToExceptionHandler());

        // If the current chat context is being removed, we need to set a new current context
        if (metadata.Id == _current?.Metadata.Id)
        {
            await LoadRecentAsCurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        OnPropertyChanged(nameof(AllHistory));
        OnPropertyChanged(nameof(RecentHistory));
        RemoveCommand.NotifyCanExecuteChanged();
    }

    public bool CanRemoveSelected => LoadedMetadata.AsValueEnumerable().Any(m => m.IsSelected);

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync(CancellationToken cancellationToken)
    {
        foreach (var metadata in LoadedMetadata.AsValueEnumerable().Where(m => m.IsSelected).ToList())
        {
            await RemoveAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads the most recently modified chat context as current.
    /// </summary>
    private async Task LoadRecentAsCurrentAsync(CancellationToken cancellationToken)
    {
        _current = null;

        if (LoadedMetadata.OrderByDescending(c => c.DateModified).FirstOrDefault() is { } historyItem)
        {
            // Switch to the most recently modified chat context
            _current = await LoadChatContextAsync(historyItem.Id, false, cancellationToken).ConfigureAwait(false);
        }

        if (_current is null)
        {
            // If no other chat context exists, create a new one
            CreateNew();
            // CreateNew will notify the change
        }
        else
        {
            NotifyCurrentChanged();
        }
    }

    public Task<ChatContext?> LoadChatContextAsync(ChatContextMetadata metadata, CancellationToken cancellationToken = default) =>
        metadata.Id == _current?.Metadata.Id ? Task.FromResult<ChatContext?>(_current) : LoadChatContextAsync(metadata.Id, false, cancellationToken);

    public string EnsureWorkingDirectory(ChatContext chatContext) =>
        _runtimeConstantProvider.EnsureWritableDataFolderPath($"plugins/{chatContext.Metadata.DateCreated:yyyy-MM-dd}");

    public void PopulateSystemPrompt(ChatContext chatContext, string? systemPrompt)
    {
        var variables =
            ImmutableDictionary.CreateRange(
                new KeyValuePair<string, Func<string>>[]
                {
                    new("Time", () => DateTime.Now.ToString("F")),
                    new("OS", () => Environment.OSVersion.ToString()),
                    new("SystemLanguage", () => LocaleManager.CurrentLocale.ToEnglishName()),
                    new("WorkingDirectory", () => EnsureWorkingDirectory(chatContext))
                });
        if (systemPrompt.IsNullOrWhiteSpace()) systemPrompt = Prompts.DefaultSystemPrompt;
        chatContext.SystemPrompt = Prompts.RenderPrompt(systemPrompt, variables);
    }

    private async Task<ChatContext?> LoadChatContextAsync(Guid id, bool deleteIfFailed, CancellationToken cancellationToken)
    {
        try
        {
            var chatContext = await _chatContextStorage.GetChatContextAsync(id, cancellationToken).ConfigureAwait(false);
            if (IsEmptyContext(chatContext))
            {
                await _chatContextStorage.DeleteChatContextAsync(id, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return chatContext;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to load chat context {ChatContextId}", id);

            await Dispatcher.UIThread.InvokeOnDemandAsync(() =>
            {
                ServiceLocator
                    .Resolve<ToastManager>()
                    .CreateToast(LocaleResolver.Common_Error)
                    .WithContent(
                        new FormattedDynamicResourceKey(
                            LocaleKey.ChatContextManager_LoadChatContextFailedToast_Content,
                            ex.GetFriendlyMessage()).ToTextBlock())
                    .ShowError();
            });

            if (deleteIfFailed) await _chatContextStorage.DeleteChatContextAsync(id, cancellationToken).ConfigureAwait(false);

            return null;
        }
    }

    /// <summary>
    /// Notifies that the current chat context has changed.
    /// </summary>
    private void NotifyCurrentChanged()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentMetadata));
        Dispatcher.UIThread.InvokeOnDemand(() =>
        {
            RemoveCommand.NotifyCanExecuteChanged();
            CreateNewCommand.NotifyCanExecuteChanged();
        });
    }

    private Task LoadMetadataAsync(int count, Guid? startAfterId) => Task.Run(async () =>
    {
        await foreach (var metadata in _chatContextStorage.QueryChatContextsAsync(count, ChatContextOrderBy.UpdatedAt, true, startAfterId))
        {
            _metadataMap[metadata.Id] = metadata;
        }

        OnPropertyChanged(nameof(AllHistory));
        OnPropertyChanged(nameof(RecentHistory));
        RemoveCommand.NotifyCanExecuteChanged();
    });

    private ObservableCollection<ChatContextHistory> ApplyHistory(ObservableCollection<ChatContextHistory> targetList, int count)
    {
        var currentDate = DateTimeOffset.UtcNow;

        // 1. Generate the desired state
        var newHistoryGroups = LoadedMetadata
            .AsValueEnumerable()
            .OrderByDescending(c => c.DateModified)
            .Take(count)
            .GroupBy(c => (currentDate - c.DateModified).TotalDays switch
            {
                < 1 => HumanizedDate.Today,
                < 2 => HumanizedDate.Yesterday,
                < 7 => HumanizedDate.LastWeek,
                < 30 => HumanizedDate.LastMonth,
                < 365 => HumanizedDate.LastYear,
                _ => HumanizedDate.Earlier
            })
            .Select(g => new
            {
                GroupKey = g.Key,
                Items = g.AsValueEnumerable().ToList()
            })
            .OrderBy(g => g.GroupKey)
            .ToList();

        var newGroupsDict = newHistoryGroups.ToDictionary(g => g.GroupKey);
        var oldGroupsDict = targetList.ToDictionary(g => g.Date);

        // 2. Remove groups that no longer exist
        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            var oldGroup = targetList[i];
            if (!newGroupsDict.ContainsKey(oldGroup.Date))
            {
                targetList.RemoveAt(i);
            }
        }

        // 3. Add new groups and update existing ones
        for (var i = 0; i < newHistoryGroups.Count; i++)
        {
            var newGroup = newHistoryGroups[i];
            if (oldGroupsDict.TryGetValue(newGroup.GroupKey, out var existingGroup))
            {
                // Group exists, sync its inner list
                SyncMetadata(existingGroup.MetadataList, newGroup.Items);
            }
            else
            {
                // Group is new, insert it at the correct sorted position
                targetList.Insert(i, new ChatContextHistory(newGroup.GroupKey, new ObservableCollection<ChatContextMetadata>(newGroup.Items)));
            }
        }

        return targetList;
    }

    /// <summary>
    /// Synchronizes a target collection of ChatContextMetadata with a new list.
    /// </summary>
    private static void SyncMetadata(ObservableCollection<ChatContextMetadata> targetList, List<ChatContextMetadata> newList)
    {
        // A simple but effective sync: clear and add.
        // Since metadata items are sorted by DateModified descending, and this order is stable
        // for existing items, we can check if we just need to append.
        if (targetList.Count > 0 && newList.Count > targetList.Count &&
            newList.AsValueEnumerable().Take(targetList.Count).SequenceEqual(targetList))
        {
            // This is an append operation (e.g., "Load More")
            for (var i = targetList.Count; i < newList.Count; i++)
            {
                targetList.Add(newList[i]);
            }
            return;
        }

        // For more complex changes (deletions, reordering), a full sync is safer.
        // This is a common and robust strategy for syncing observable collections.
        var newItemsDict = newList.ToDictionary(item => item.Id);
        var oldItemsDict = targetList.ToDictionary(item => item.Id);

        // Remove items that are no longer in the new list
        for (var i = targetList.Count - 1; i >= 0; i--)
        {
            if (!newItemsDict.ContainsKey(targetList[i].Id))
            {
                targetList.RemoveAt(i);
            }
        }

        // Add new items and re-order existing ones
        for (var i = 0; i < newList.Count; i++)
        {
            var newItem = newList[i];
            if (oldItemsDict.TryGetValue(newItem.Id, out var oldItem))
            {
                // Item exists, check if it's at the right position
                var currentIndex = targetList.IndexOf(oldItem);
                if (currentIndex != i)
                {
                    targetList.Move(currentIndex, i);
                }
            }
            else
            {
                // Item is new, insert it
                targetList.Insert(i, newItem);
            }
        }
    }

    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Startup;

    public Task InitializeAsync() => LoadMetadataAsync(9, null);

    private static bool IsEmptyContext([NotNullWhen(true)] ChatContext? chatContext) => chatContext is { Count: 1 };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex WorkingDirectoryRegex();
}

public static class ChatContextManagerExtension
{
    public static IServiceCollection AddChatContextManager(this IServiceCollection services)
    {
        services.AddSingleton<ChatContextManager>();
        services.AddSingleton<IChatContextManager>(x => x.GetRequiredService<ChatContextManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatContextManager>());
        return services;
    }
}