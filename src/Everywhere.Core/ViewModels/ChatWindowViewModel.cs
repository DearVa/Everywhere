using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Storage;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

public sealed partial class ChatWindowViewModel :
    BusyViewModelBase,
    IRecipient<ChatPluginConsentRequest>,
    IRecipient<ChatContextMetadataChangedMessage>,
    IRecipient<ApplicationCommand>,
    IObserver<TextSelectionData>,
    IDisposable
{
    public Settings Settings { get; }
    public PersistentState PersistentState { get; }

    public bool IsOpened
    {
        get;
        set
        {
            field = value;
            // notify property changed even if the value is the same
            // so that the view can update its visibility and topmost
            OnPropertyChanged();

            if (value)
            {
                _openActivity ??= _activitySource.StartActivity();
            }
            else
            {
                DisposeCollector.DisposeToDefault(ref _openActivity);
            }
        }
    }

    /// <summary>
    /// Indicates whether the chat window is currently viewing history page.
    /// </summary>
    [ObservableProperty]
    public partial bool IsViewingHistory { get; set; }

    public bool? IsAllHistorySelected
    {
        get
        {
            bool? value = null;
            foreach (var metadata in ChatContextManager.AllHistory.AsValueEnumerable().SelectMany(h => h.MetadataList))
            {
                if (metadata.IsSelected)
                {
                    if (value == false) return null;
                    value = true;
                }
                else
                {
                    if (value == true) return null;
                    value = false;
                }
            }
            return value;
        }
        set
        {
            if (!value.HasValue) return; // do nothing for indeterminate state
            ChatContextManager.AllHistory.SelectMany(h => h.MetadataList).ForEach(m => m.IsSelected = value.Value);
        }
    }

    /// <summary>
    /// Indicates whether the file picker is currently open.
    /// </summary>
    public bool IsPickingFiles { get; set; }

    public ReadOnlyObservableCollection<ChatAttachment> ChatAttachments { get; }

    [ObservableProperty]
    public partial IReadOnlyList<DynamicNamedCommand>? QuickActions { get; private set; }

    public IChatContextManager ChatContextManager { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    public partial ChatMessageNode? EditingUserMessageNode { get; private set; }

    public bool CanEdit => IsNotBusy && EditingUserMessageNode is null;

    public static int ChatInputAreaTextMaxLength => 100_000;

    /// <summary>
    /// The text in the chat input box.
    /// </summary>
    public string? ChatInputAreaText
    {
        get;
        set
        {
            value = value.SafeSubstring(0, ChatInputAreaTextMaxLength);
            if (!SetProperty(ref field, value)) return;
            if (EditingUserMessageNode is null) PersistentState.ChatInputAreaText = value;
        }
    }

    private readonly IChatService _chatService;
    private readonly IVisualElementContext _visualElementContext;
    private readonly INativeHelper _nativeHelper;
    private readonly IBlobStorage _blobStorage;
    private readonly ILogger<ChatWindowViewModel> _logger;

    private readonly CompositeDisposable _disposables = new(2);
    private readonly SourceList<ChatAttachment> _chatAttachmentsSource = new();
    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();
    private readonly ActivitySource _activitySource = new(typeof(ChatWindowViewModel).FullName.NotNull());

    private List<ChatAttachment>? _chatAttachmentsBeforeEditing;

    /// <summary>
    /// Start an activity when the window is opened, and dispose it when closed.
    /// </summary>
    private Activity? _openActivity;

    public ChatWindowViewModel(
        Settings settings,
        PersistentState persistentState,
        IChatContextManager chatContextManager,
        IChatService chatService,
        IVisualElementContext visualElementContext,
        INativeHelper nativeHelper,
        IBlobStorage blobStorage,
        ILogger<ChatWindowViewModel> logger)
    {
        Settings = settings;
        PersistentState = persistentState;
        ChatContextManager = chatContextManager;
        ChatContextManager.PropertyChanged += HandleChatContextManagerPropertyChanged;

        _chatService = chatService;
        _visualElementContext = visualElementContext;
        _nativeHelper = nativeHelper;
        _blobStorage = blobStorage;
        _logger = logger;

        // Load the saved input box text
        ChatInputAreaText = PersistentState.ChatInputAreaText;

        ChatAttachments = _chatAttachmentsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);

        _disposables.Add(_chatAttachmentsSource);

        WeakReferenceMessenger.Default.RegisterAll(this);

        InitializeCommands();
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _cancellationTokenSource.Dispose();
        _targetElementChangedTokenSource?.Dispose();
        ChatContextManager.PropertyChanged -= HandleChatContextManagerPropertyChanged;
    }

    private void InitializeCommands()
    {
        QuickActions =
        [
            new DynamicNamedCommand(
                LucideIconKind.Languages,
                LocaleKey.ChatWindowViewModel_QuickActions_Translate,
                null,
                SendMessageCommand,
                $"Please translate the focal elements and related content into {Settings.Common.Language.ToEnglishName()}. " +
                $"If it's already in target language, translate it to English. " +
                $"Provide only the translation, do not include any other text or explanation."
            ),
            new DynamicNamedCommand(
                LucideIconKind.ScrollText,
                LocaleKey.ChatWindowViewModel_QuickActions_Summarize,
                null,
                SendMessageCommand,
                "Please summarize the key elements and related content into a paragraph and extract several key points. " +
                "Provide only the summary, do not include any other text or explanation."
            ),
            new DynamicNamedCommand(
                LucideIconKind.SearchCheck,
                LocaleKey.ChatWindowViewModel_QuickActions_Verify,
                null,
                SendMessageCommand,
                "Please verify the authenticity of the focal elements and related content, and point out any suspicious or incorrect parts."
            ),
            new DynamicNamedCommand(
                LucideIconKind.Sparkle,
                LocaleKey.ChatWindowViewModel_QuickActions_Solve,
                null,
                SendMessageCommand,
                "Please solve the problem described by the focal elements and related content. " +
                "If no problem is described, provide some relevant suggestions or improvements."
            ),
        ];
    }

    private CancellationTokenSource? _targetElementChangedTokenSource;

    /// <summary>
    /// Show the chat window and float to the target element.
    /// </summary>
    /// <param name="targetElement"></param>
    public async Task ShowAsync(IVisualElement? targetElement)
    {
        // debouncing
        if (_targetElementChangedTokenSource is not null) await _targetElementChangedTokenSource.CancelAsync();
        _targetElementChangedTokenSource = new CancellationTokenSource();
        var cancellationToken = _targetElementChangedTokenSource.Token;
        try
        {
            await Task.Delay(100, cancellationToken);
        }
        catch (OperationCanceledException) { }

        try
        {
            if (Settings.ChatWindow.AlwaysStartNewChat && ChatContextManager.CreateNewCommand.CanExecute(null))
            {
                ChatContextManager.CreateNewCommand.Execute(null);
            }

            IsOpened = true;

            // Avoid adding duplicate attachments
            if (_chatAttachmentsSource.Items.Any(a => a is ChatVisualElementAttachment vea && Equals(vea.Element?.Target, targetElement))) return;

            if (targetElement == null)
            {
                _chatAttachmentsSource.Edit(list =>
                {
                    if (list is [ChatVisualElementAttachment { IsPrimary: true }, ..])
                    {
                        list.RemoveAt(0);
                    }
                });
                return;
            }

            var createElement = Settings.ChatWindow.AutomaticallyAddElement;
            var attachment = await Task
                .Run(() => createElement ? CreateFromVisualElement(targetElement) : null, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

            if (attachment is not null)
            {
                _chatAttachmentsSource.Edit(list =>
                {
                    list.RemoveWhere(a => a is ChatVisualElementAttachment { IsPrimary: true });
                    list.Insert(0, attachment.With(a => a.IsPrimary = true));
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float to target element.");
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task PickElementAsync() => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var chatWindow = ServiceLocator.Resolve<ChatWindow>();
            var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
            windowHelper.SetCloaked(chatWindow, true);
            var element = await _visualElementContext.PickElementAsync(null);
            windowHelper.SetCloaked(chatWindow, false);

            if (element is null) return;
            if (_chatAttachmentsSource.Items.OfType<ChatVisualElementAttachment>().Any(a => Equals(a.Element?.Target, element))) return;
            _chatAttachmentsSource.Add(await Task.Run(() => CreateFromVisualElement(element), cancellationToken));
        },
        _logger.ToExceptionHandler());

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task ScreenshotAsync() => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var chatWindow = ServiceLocator.Resolve<ChatWindow>();
            var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
            windowHelper.SetCloaked(chatWindow, true);
            var bitmap = await _visualElementContext.ScreenshotAsync(null);
            windowHelper.SetCloaked(chatWindow, false);

            if (bitmap is null) return;
            _chatAttachmentsSource.Add(await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
        },
        _logger.ToExceptionHandler());

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task AddClipboardAsync() => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            var formats = await Clipboard.GetDataFormatsAsync();
            if (formats.Count == 0)
            {
                _logger.LogWarning("Clipboard is empty.");
                return;
            }

            if (formats.Contains(DataFormat.File))
            {
                var files = await Clipboard.TryGetFilesAsync();
                if (files != null)
                {
                    foreach (var storageItem in files)
                    {
                        var uri = storageItem.Path;
                        if (!uri.IsFile) break;
                        await AddFileUncheckAsync(uri.LocalPath, "from clipboard, temporary filepath", cancellationToken);
                        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                    }
                }
            }
            else if (Settings.Model.SelectedCustomAssistant?.IsImageInputSupported is true &&
                     formats.Contains(DataFormat.Bitmap) &&
                     await Clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                _chatAttachmentsSource.Add(await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
            }

            // TODO: add as text attachment when text is too long
            // else if (formats.Contains(DataFormats.Text))
            // {
            //     var text = await Clipboard.GetTextAsync();
            //     if (text.IsNullOrEmpty()) return;
            //
            //     chatAttachments.Add(new ChatTextAttachment(new DirectResourceKey(text.SafeSubstring(0, 10)), text));
            // }
        },
        _logger.ToExceptionHandler());

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task AddFileAsync()
    {
        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

        IReadOnlyList<IStorageFile> files;
        IsPickingFiles = true;
        try
        {
            files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_SupportedFiles)
                        {
                            Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                .AsValueEnumerable()
                                .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document))
                                .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Images)
                        {
                            Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                .AsValueEnumerable()
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Documents)
                        {
                            Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document)
                                .AsValueEnumerable()
                                .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_AllFiles)
                        {
                            Patterns = ["*"]
                        }
                    ]
                });
        }
        finally
        {
            IsPickingFiles = false;
        }

        if (files.Count <= 0) return;
        if (files[0].TryGetLocalPath() is not { } filePath)
        {
            _logger.LogWarning("File path is not available.");
            return;
        }

        await AddFileUncheckAsync(filePath, cancellationToken: _cancellationTokenSource.Token);
    }

    /// <summary>
    /// Add a file to the chat attachments without checking the attachment count limit.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="description"></param>
    /// <param name="cancellationToken"></param>
    private async ValueTask AddFileUncheckAsync(string filePath, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            _chatAttachmentsSource.Add(
                await ChatFileAttachment.CreateAsync(
                    filePath,
                    description: description,
                    cancellationToken: cancellationToken));
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);

            _logger.LogError(ex, "Failed to load image from file: {FilePath}", filePath);
            ToastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
        }
    }

    /// <summary>
    /// Add a file to the chat attachments from drag and drop.
    /// Checks the attachment count limit.
    /// </summary>
    /// <param name="filePath">The file path to add.</param>
    public async Task AddFileFromDragDropAsync(string filePath)
    {
        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

        await AddFileUncheckAsync(filePath, "from drag&drop", _cancellationTokenSource.Token);
    }

    private static ChatVisualElementAttachment CreateFromVisualElement(IVisualElement element)
    {
        DynamicResourceKey headerKey;
        var elementTypeKey = new DynamicResourceKey($"VisualElementType_{element.Type}");
        if (element.ProcessId > 0)
        {
            using var process = Process.GetProcessById(element.ProcessId);
            headerKey = new FormattedDynamicResourceKey("{0} - {1}", new DirectResourceKey(process.ProcessName), elementTypeKey);
        }
        else
        {
            headerKey = elementTypeKey;
        }

        return new ChatVisualElementAttachment(
            headerKey,
            element.Type switch
            {
                VisualElementType.Label => LucideIconKind.Type,
                VisualElementType.TextEdit => LucideIconKind.TextInitial,
                VisualElementType.Document => LucideIconKind.FileText,
                VisualElementType.Image => LucideIconKind.Image,
                VisualElementType.CheckBox => LucideIconKind.SquareCheck,
                VisualElementType.RadioButton => LucideIconKind.CircleCheckBig,
                VisualElementType.ComboBox => LucideIconKind.ChevronDown,
                VisualElementType.ListView => LucideIconKind.List,
                VisualElementType.ListViewItem => LucideIconKind.List,
                VisualElementType.TreeView => LucideIconKind.ListTree,
                VisualElementType.TreeViewItem => LucideIconKind.ListTree,
                VisualElementType.DataGrid => LucideIconKind.Table,
                VisualElementType.DataGridItem => LucideIconKind.Table,
                VisualElementType.TabControl or VisualElementType.TabItem => LucideIconKind.LayoutPanelTop,
                VisualElementType.Table => LucideIconKind.Table,
                VisualElementType.TableRow => LucideIconKind.Table,
                VisualElementType.Menu => LucideIconKind.Menu,
                VisualElementType.MenuItem => LucideIconKind.Menu,
                VisualElementType.Slider => LucideIconKind.SlidersHorizontal,
                VisualElementType.ScrollBar => LucideIconKind.Settings2,
                VisualElementType.ProgressBar => LucideIconKind.Percent,
                VisualElementType.Panel => LucideIconKind.Group,
                VisualElementType.TopLevel => LucideIconKind.AppWindow,
                VisualElementType.Screen => LucideIconKind.Monitor,
                _ => LucideIconKind.Component
            },
            element);
    }

    private async Task<ChatFileAttachment> CreateFromBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, 100);

        var blob = await _blobStorage.StorageBlobAsync(memoryStream, "image/png", cancellationToken);
        return new ChatFileAttachment(
            new DynamicResourceKey(string.Empty),
            blob.LocalPath,
            blob.Sha256,
            blob.MimeType);
    }

    [RelayCommand]
    private void RemoveAttachment(ChatAttachment attachment)
    {
        _chatAttachmentsSource.Remove(attachment);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task SendMessage(string? message) => ExecuteBusyTaskAsync(
        cancellationToken =>
        {
            message = message?.Trim();
            if (message?.Length is not > 0) return Task.CompletedTask;

            ImmutableArray<ChatAttachment> attachments = [];
            _chatAttachmentsSource.Edit(list =>
            {
                attachments = [..list];
                list.Clear();
            });

            var userMessage = new UserChatMessage(message, attachments);

            if (EditingUserMessageNode is not { } originalNode)
            {
                return Task.Run(() => _chatService.SendMessageAsync(userMessage, cancellationToken), cancellationToken);
            }

            CancelEditing();

            return Task.Run(() => _chatService.EditAsync(originalNode, userMessage, cancellationToken), cancellationToken);
        },
        _logger.ToExceptionHandler(),
        cancellationToken: _cancellationTokenSource.Token);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit(ChatMessageNode userChatMessageNode)
    {
        if (userChatMessageNode is not { Message: UserChatMessage userChatMessage }) return;

        EditingUserMessageNode = userChatMessageNode;
        ChatInputAreaText = userChatMessage.Content;
        _chatAttachmentsSource.Edit(list =>
        {
            _chatAttachmentsBeforeEditing = list.ToList();
            list.Clear();
            list.AddRange(userChatMessage.Attachments.Where(a => a is not ChatVisualElementAttachment { IsElementValid: false }));
        });
    }

    [RelayCommand]
    public void CancelEditing()
    {
        if (EditingUserMessageNode is null) return;

        EditingUserMessageNode = null;
        _chatAttachmentsSource.Edit(list =>
        {
            list.Clear();
            if (_chatAttachmentsBeforeEditing is not null)
            {
                list.AddRange(_chatAttachmentsBeforeEditing);
                _chatAttachmentsBeforeEditing = null;
            }
        });

        ChatInputAreaText = PersistentState.ChatInputAreaText;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RetryAsync(ChatMessageNode chatMessageNode) => ExecuteBusyTaskAsync(
        cancellationToken => Task.Run(() => _chatService.RetryAsync(chatMessageNode, cancellationToken), cancellationToken),
        _logger.ToExceptionHandler(),
        cancellationToken: _cancellationTokenSource.Token);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }

    [RelayCommand]
    private Task CopyAsync(ChatMessage chatMessage)
    {
        return Clipboard.SetTextAsync(chatMessage.ToString());
    }

    [RelayCommand]
    private void OpenSettings()
    {
        WeakReferenceMessenger.Default.Send<ApplicationCommand>(new ShowWindowCommand(nameof(MainView)));
    }

    [RelayCommand]
    private void SwitchViewingHistory(object? value)
    {
        IsViewingHistory = Convert.ToBoolean(value);
    }


    [RelayCommand]
    private async Task ExportMarkdownAsync(ChatContextMetadata metadata)
    {
        var chatContext = await ChatContextManager.LoadChatContextAsync(metadata, _cancellationTokenSource.Token);
        if (chatContext is null)
        {
            ToastManager
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FailedToLoadChatContext)
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTopicName = string.Join("_", (metadata.Topic ?? "chat").Split(Path.GetInvalidFileNameChars()));
        var suggestedFileName = $"{safeTopicName}_{timestamp}.md";
        var exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), suggestedFileName);

        IsPickingFiles = true;
        IStorageFile? storageFile;
        try
        {
            storageFile = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    SuggestedFileName = suggestedFileName,
                    FileTypeChoices =
                    [
                        new FilePickerFileType(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FilePickerFileType_Markdown)
                        {
                            Patterns = ["*.md"]
                        },
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_AllFiles)
                        {
                            Patterns = ["*"]
                        }
                    ],
                    DefaultExtension = ".md",
                    SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents)
                });
        }
        finally
        {
            IsPickingFiles = false;
        }

        if (storageFile is null) return;

        var markdownBuilder = new StringBuilder();

        var topic = metadata.Topic ?? LocaleResolver.ChatContext_Metadata_Topic_Default;
        markdownBuilder.AppendLine($"# {topic}");
        markdownBuilder.AppendLine();
        markdownBuilder.AppendLine($"**{LocaleResolver.ChatWindowViewModel_ExportMarkdown_DateCreated}:** {metadata.DateCreated:F}");
        markdownBuilder.AppendLine($"**{LocaleResolver.ChatWindowViewModel_ExportMarkdown_DateModified}:** {metadata.DateModified:F}");
        markdownBuilder.AppendLine();
        markdownBuilder.AppendLine("---");
        markdownBuilder.AppendLine();

        foreach (var chatMessage in chatContext
                     .GetAllNodes()
                     .AsValueEnumerable()
                     .Select(node => node.Message))
        {
            switch (chatMessage)
            {
                case UserChatMessage user:
                {
                    markdownBuilder.AppendLine($"## 👤 {LocaleResolver.ChatWindowViewModel_ExportMarkdown_UserRole}");
                    markdownBuilder.AppendLine();
                    markdownBuilder.AppendLine(user.Content);

                    if (user.Attachments.Any())
                    {
                        markdownBuilder.AppendLine();
                        markdownBuilder.AppendLine($"**{LocaleResolver.ChatWindowViewModel_ExportMarkdown_UserAttachments}:**");
                        foreach (var attachment in user.Attachments)
                        {
                            markdownBuilder.AppendLine($"- {attachment.HeaderKey}");
                        }
                    }

                    markdownBuilder.AppendLine();
                    break;
                }
                case AssistantChatMessage assistant:
                {
                    if (assistant.Spans.AsValueEnumerable().All(span => span.MarkdownBuilder.Length == 0 && span.FunctionCalls.Count == 0))
                        break;
                    markdownBuilder.AppendLine($"## 🤖 {LocaleResolver.ChatWindowViewModel_ExportMarkdown_AssistantRole}");
                    markdownBuilder.AppendLine();

                    // ReSharper disable once ForCanBeConvertedToForeach
                    // foreach would create an enumerator object, which will cause thread lock issues.
                    for (var spanIndex = 0; spanIndex < assistant.Spans.Count; spanIndex++)
                    {
                        var span = assistant.Spans[spanIndex];
                        if (span.MarkdownBuilder.Length > 0)
                        {
                            markdownBuilder.AppendLine(span.MarkdownBuilder.ToString());
                        }

                        // ReSharper disable once ForCanBeConvertedToForeach
                        // foreach would create an enumerator object, which will cause thread lock issues.
                        for (var callIndex = 0; callIndex < span.FunctionCalls.Count; callIndex++)
                        {
                            var functionCall = span.FunctionCalls[callIndex];
                            markdownBuilder.AppendLine(
                                $"***{LocaleResolver.ChatWindowViewModel_ExportMarkdown_FunctionCall}:** {functionCall.HeaderKey}*");

                            if (functionCall.ErrorMessageKey is not null)
                            {
                                markdownBuilder.AppendLine();
                                markdownBuilder.AppendLine(
                                    $"**{LocaleResolver.ChatWindowViewModel_ExportMarkdown_ErrorMessage}:** {functionCall.ErrorMessageKey}");
                            }
                        }
                    }

                    markdownBuilder.AppendLine();
                    break;
                }
            }
        }

        var markdownContent = markdownBuilder.ToString();

        try
        {
            await using var stream = await storageFile.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(markdownContent);
            exportPath = storageFile.TryGetLocalPath() ?? exportPath;
        }
        catch (Exception e)
        {
            e = HandledSystemException.Handle(e);

            _logger.LogError(e, "Failed to export chat context to markdown file.");

            ToastManager
                .CreateToast(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FailedToSaveFile)
                .WithContent(e.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        // Show success toast and open file
        ToastManager
            .CreateToast(LocaleResolver.ChatWindowViewModel_ExportMarkdown_ExportSuccess)
            .WithContent(exportPath)
            .DismissOnClick()
            .OnBottomRight()
            .ShowSuccess();

        await ServiceLocator.Resolve<ILauncher>().LaunchFileInfoAsync(new FileInfo(exportPath));
    }

    public void Receive(ChatContextMetadataChangedMessage message)
    {
        if (message.PropertyName == nameof(ChatContextMetadata.IsSelected)) OnPropertyChanged(nameof(IsAllHistorySelected));
    }

    private void HandleChatContextManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IChatContextManager.AllHistory)) OnPropertyChanged(nameof(IsAllHistorySelected));
    }

    [RelayCommand]
    private void Close()
    {
        IsOpened = false;
        if (!Settings.ChatWindow.AllowRunInBackground) _cancellationTokenSource.Cancel();
    }

    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();

        PickElementCommand.NotifyCanExecuteChanged();
        ScreenshotCommand.NotifyCanExecuteChanged();
        AddClipboardCommand.NotifyCanExecuteChanged();
        AddFileCommand.NotifyCanExecuteChanged();
        SendMessageCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Receive(ChatPluginConsentRequest message)
    {
        Dispatcher.UIThread.InvokeOnDemand(() =>
        {
            var card = new ConsentDecisionCard
            {
                Header = message.HeaderKey.ToTextBlock(),
                Content = message.Content,
                CanRemember = message.CanRemember
            };
            card.ConsentSelected += (_, args) =>
            {
                message.Promise.TrySetResult(args.Decision);
                DialogManager.Close(card);
            };
            DialogManager
                .CreateCustomDialog(card)
                .ShowAsync(message.CancellationToken);

            if (!IsOpened)
            {
                _nativeHelper
                    .ShowDesktopNotificationAsync(message.HeaderKey.ToString() ?? LocaleResolver.Common_Info)
                    .ContinueWith(r =>
                    {
                        if (r is { IsFaulted: false, Result: true }) Dispatcher.UIThread.Invoke(() => IsOpened = true);
                    });
            }
        });
    }

    #region IObserver<TextSelectionData> Implementation

    void IObserver<TextSelectionData>.OnCompleted() { }

    void IObserver<TextSelectionData>.OnError(Exception error) { }

    void IObserver<TextSelectionData>.OnNext(TextSelectionData data)
    {
        Console.WriteLine(data);

        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;
        if (data.Element?.ProcessId == Environment.ProcessId) return; // Ignore selections from this app

        _chatAttachmentsSource.Edit(list =>
        {
            // Remove existing text selection attachment
            list.RemoveWhere(a => a is ChatTextSelectionAttachment);

            // Insert the new attachment at the beginning if it has text
            if (!data.Text.IsNullOrEmpty()) list.Insert(0, new ChatTextSelectionAttachment(data.Text, data.Element));
        });
    }

    #endregion

    public void Receive(ApplicationCommand command)
    {
        if (command is ShowWindowCommand { Name: nameof(ChatWindowViewModel) })
        {
            Dispatcher.UIThread.Invoke(() => IsOpened = true);
        }
    }
}