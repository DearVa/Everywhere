using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    IDisposable
{
    public Settings Settings { get; }

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

    [ObservableProperty]
    public partial PixelRect TargetBoundingRect { get; private set; }

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

    private readonly IChatService _chatService;
    private readonly IVisualElementContext _visualElementContext;
    private readonly INativeHelper _nativeHelper;
    private readonly IBlobStorage _blobStorage;
    private readonly ILogger<ChatWindowViewModel> _logger;

    private readonly CompositeDisposable _disposables = new(2);
    private readonly SourceList<ChatAttachment> _chatAttachmentsSource = new();
    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();
    private readonly ActivitySource _activitySource = new(typeof(ChatWindowViewModel).FullName.NotNull());

    /// <summary>
    /// Start an activity when the window is opened, and dispose it when closed.
    /// </summary>
    private Activity? _openActivity;

    public ChatWindowViewModel(
        Settings settings,
        IChatContextManager chatContextManager,
        IChatService chatService,
        IVisualElementContext visualElementContext,
        INativeHelper nativeHelper,
        IBlobStorage blobStorage,
        ILogger<ChatWindowViewModel> logger)
    {
        Settings = settings;
        ChatContextManager = chatContextManager;
        ChatContextManager.PropertyChanged += HandleChatContextManagerPropertyChanged;

        _chatService = chatService;
        _visualElementContext = visualElementContext;
        _nativeHelper = nativeHelper;
        _blobStorage = blobStorage;
        _logger = logger;

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

    public async Task TryFloatToTargetElementAsync(IVisualElement? targetElement)
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
            IsOpened = true;

            // Avoid adding duplicate attachments
            if (_chatAttachmentsSource.Items.Any(a => a is ChatVisualElementAttachment vea && Equals(vea.Element?.Target, targetElement))) return;

            TargetBoundingRect = default;
            if (targetElement == null)
            {
                _chatAttachmentsSource.Edit(list =>
                {
                    if (list is [ChatVisualElementAttachment { IsFocusedElement: true }, ..])
                    {
                        list.RemoveAt(0);
                    }
                });
                return;
            }

            var createElement = Settings.ChatWindow.AutomaticallyAddElement;
            var (boundingRect, attachment) = await Task
                .Run(() => (targetElement.BoundingRectangle, createElement ? CreateFromVisualElement(targetElement) : null), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            TargetBoundingRect = boundingRect;
            if (attachment is not null)
            {
                _chatAttachmentsSource.Edit(list =>
                {
                    if (list is [ChatVisualElementAttachment { IsFocusedElement: true }, ..])
                    {
                        list[0] = attachment.With(a => a.IsFocusedElement = true);
                    }
                    else
                    {
                        list.Insert(0, attachment.With(a => a.IsFocusedElement = true));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float to target element.");
        }
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task PickElementAsync(PickElementMode mode) => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (_chatAttachmentsSource.Count >= Settings.Internal.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var chatWindow = ServiceLocator.Resolve<ChatWindow>();
            var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
            windowHelper.SetCloaked(chatWindow, true);
            var element = await _visualElementContext.PickElementAsync(mode);
            windowHelper.SetCloaked(chatWindow, false);

            if (element is null) return;
            if (_chatAttachmentsSource.Items.OfType<ChatVisualElementAttachment>().Any(a => Equals(a.Element?.Target, element))) return;
            _chatAttachmentsSource.Add(await Task.Run(() => CreateFromVisualElement(element), cancellationToken));
        },
        _logger.ToExceptionHandler());

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task AddClipboardAsync() => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (_chatAttachmentsSource.Count >= Settings.Internal.MaxChatAttachmentCount) return;

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
                        await AddFileUncheckAsync(uri.AbsolutePath);
                        if (_chatAttachmentsSource.Count >= Settings.Internal.MaxChatAttachmentCount) break;
                    }
                }
            }
            else if (Settings.Model.SelectedCustomAssistant?.IsImageInputSupported.ActualValue is true)
            {
                if (await _nativeHelper.GetClipboardBitmapAsync() is not { } bitmap) return;

                await Task.Run(
                    async () =>
                    {
                        using var memoryStream = new MemoryStream();
                        bitmap.Save(memoryStream, 100);

                        var blob = await _blobStorage.StorageBlobAsync(memoryStream, "image/png", cancellationToken);

                        var attachment = new ChatFileAttachment(
                            new DynamicResourceKey(string.Empty),
                            blob.LocalPath,
                            blob.Sha256,
                            blob.MimeType);
                        _chatAttachmentsSource.Add(attachment);
                    },
                    cancellationToken);
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
        if (_chatAttachmentsSource.Count >= Settings.Internal.MaxChatAttachmentCount) return;

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
                        new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_SupportedFiles.I18N())
                        {
                            Patterns = MimeTypeUtilities.SupportedMimeTypes.Keys
                                .AsValueEnumerable()
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_Images.I18N())
                        {
                            Patterns = MimeTypeUtilities.GetExtensionsForMimeTypePrefix("image/")
                                .AsValueEnumerable()
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_Documents.I18N())
                        {
                            Patterns = MimeTypeUtilities.GetExtensionsForMimeTypePrefix("application/")
                                .AsValueEnumerable()
                                .Concat(MimeTypeUtilities.GetExtensionsForMimeTypePrefix("text/"))
                                .Select(x => '*' + x)
                                .ToList()
                        },
                        new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_AllFiles.I18N())
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

        await AddFileUncheckAsync(filePath);
    }

    /// <summary>
    /// Add a file to the chat attachments without checking the attachment count limit.
    /// </summary>
    /// <param name="filePath"></param>
    private async ValueTask AddFileUncheckAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            _chatAttachmentsSource.Add(await ChatFileAttachment.CreateAsync(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load image from file: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Add a file to the chat attachments from drag and drop.
    /// Checks the attachment count limit.
    /// </summary>
    /// <param name="filePath">The file path to add.</param>
    public async Task AddFileFromDragDropAsync(string filePath)
    {
        if (_chatAttachmentsSource.Count >= Settings.Internal.MaxChatAttachmentCount) return;
        await AddFileUncheckAsync(filePath);
    }

    private static ChatVisualElementAttachment CreateFromVisualElement(IVisualElement element)
    {
        DynamicResourceKey headerKey;
        var elementTypeKey = new DynamicResourceKey($"VisualElementType_{element.Type}");
        if (element.ProcessId != 0)
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
                VisualElementType.TextEdit => LucideIconKind.TextCursorInput,
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
                VisualElementType.TabControl => LucideIconKind.LayoutPanelTop,
                VisualElementType.TabItem => LucideIconKind.LayoutPanelTop,
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
            var userMessage = new UserChatMessage(message, attachments)
            {
                Inlines = { message }
            };

            return Task.Run(() => _chatService.SendMessageAsync(userMessage, cancellationToken), cancellationToken);
        },
        _logger.ToExceptionHandler(),
        cancellationToken: _cancellationTokenSource.Token);

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
    private Task CopyAsync(ChatMessage chatMessage) => Clipboard.SetTextAsync(chatMessage.ToString());

    [RelayCommand]
    private void SwitchViewingHistory(object? value)
    {
        IsViewingHistory = Convert.ToBoolean(value);
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

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsBusy))
        {
            PickElementCommand.NotifyCanExecuteChanged();
            AddClipboardCommand.NotifyCanExecuteChanged();
            AddFileCommand.NotifyCanExecuteChanged();
            SendMessageCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
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
                .CreateDialog(card)
                .ShowAsync(message.CancellationToken);
        });
    }
}