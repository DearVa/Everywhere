using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Models;
using Everywhere.Utils;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using ZLinq;
using ChatMessage = Everywhere.Models.ChatMessage;

namespace Everywhere.ViewModels;

public partial class ChatFloatingWindowViewModel : BusyViewModelBase
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
        }
    }

    [ObservableProperty]
    public partial PixelRect TargetBoundingRect { get; private set; }

    [field: AllowNull, MaybeNull]
    public NotifyCollectionChangedSynchronizedViewList<ChatAttachment> ChatAttachments =>
        field ??= chatAttachments.ToNotifyCollectionChangedSlim(SynchronizationContextCollectionEventDispatcher.Current);

    [ObservableProperty]
    public partial IReadOnlyList<DynamicNamedCommand>? QuickActions { get; private set; }

    [ObservableProperty]
    public partial bool IsPined { get; set; }

    public IChatContextManager ChatContextManager { get; }

    public IChatService ChatService { get; }

    private readonly IVisualElementContext visualElementContext;
    private readonly INativeHelper nativeHelper;
    private readonly ILogger<ChatFloatingWindowViewModel> logger;

    private readonly ObservableList<ChatAttachment> chatAttachments = [];
    private readonly ReusableCancellationTokenSource cancellationTokenSource = new();

    public ChatFloatingWindowViewModel(
        IChatContextManager chatContextManager,
        IChatService chatService,
        Settings settings,
        IVisualElementContext visualElementContext,
        INativeHelper nativeHelper,
        ILogger<ChatFloatingWindowViewModel> logger)
    {
        ChatContextManager = chatContextManager;
        ChatService = chatService;
        Settings = settings;

        this.visualElementContext = visualElementContext;
        this.nativeHelper = nativeHelper;
        this.logger = logger;

        InitializeCommands();
    }

    private void InitializeCommands()
    {
        DynamicNamedCommand[] textEditActions =
        [
            new(
                LucideIconKind.Languages,
                LocaleKey.ChatFloatingWindowViewModel_TextEditActions_Translate,
                null,
                SendMessageCommand,
                $"Translate the content in focused element to {new CultureInfo(Settings.Common.Language).Name}. " +
                $"If it's already in target language, translate it to English. " +
                $"You MUST only reply with the translated content, without any other text or explanation"
            ),
            new(
                LucideIconKind.StepForward,
                LocaleKey.ChatFloatingWindowViewModel_TextEditActions_ContinueWriting,
                null,
                SendMessageCommand,
                "I have already written a beginning as the content of the focused element. " +
                "You MUST imitate my writing style and tone, then continue writing in my perspective. " +
                "You MUST only reply with the continue written content, without any other text or explanation"
            ),
            new(
                LucideIconKind.ScrollText,
                LocaleKey.ChatFloatingWindowViewModel_TextEditActions_Summarize,
                null,
                SendMessageCommand,
                "Please summarize the content in focused element. " +
                "You MUST only reply with the summarize content, without any other text or explanation"
            )
        ];

        void HandleChatAttachmentsCollectionChanged(in NotifyCollectionChangedEventArgs<ChatAttachment> x)
        {
            QuickActions = chatAttachments switch
            {
                [ChatVisualElementAttachment { Element.Type: VisualElementType.TextEdit }] => textEditActions,
                _ => null
            };
        }

        chatAttachments.CollectionChanged += HandleChatAttachmentsCollectionChanged;
    }

    private CancellationTokenSource? targetElementChangedTokenSource;

    public async Task TryFloatToTargetElementAsync(IVisualElement? targetElement)
    {
        // debouncing
        if (targetElementChangedTokenSource is not null) await targetElementChangedTokenSource.CancelAsync();
        targetElementChangedTokenSource = new CancellationTokenSource();
        var cancellationToken = targetElementChangedTokenSource.Token;
        try
        {
            await Task.Delay(100, cancellationToken);
        }
        catch (OperationCanceledException) { }

        await ExecuteBusyTaskAsync(
            async token =>
            {
                if (chatAttachments.Any(a => a is ChatVisualElementAttachment vea && vea.Element.Equals(targetElement)))
                {
                    IsOpened = true;
                    return;
                }

                Reset();

                if (targetElement == null)
                {
                    return;
                }

                TargetBoundingRect = targetElement.BoundingRectangle;
                chatAttachments.Clear();
                chatAttachments.Add(await Task.Run(() => CreateFromVisualElement(targetElement), token));
                IsOpened = true;
            },
            logger.ToExceptionHandler(),
            ExecutionFlags.EnqueueIfBusy,
            cancellationToken);
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPined ^= true;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task AddElementAsync(PickElementMode mode) => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            if (chatAttachments.Count >= Settings.Internal.MaxChatAttachmentCount) return;

            if (await visualElementContext.PickElementAsync(mode) is not { } element) return;
            if (chatAttachments.OfType<ChatVisualElementAttachment>().Any(a => a.Element.Id == element.Id)) return;
            chatAttachments.Add(await Task.Run(() => CreateFromVisualElement(element), cancellationToken));
        },
        logger.ToExceptionHandler());

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task AddClipboardAsync() => ExecuteBusyTaskAsync(
        async _ =>
        {
            if (chatAttachments.Count >= Settings.Internal.MaxChatAttachmentCount) return;

            var formats = await Clipboard.GetFormatsAsync();
            if (formats.Length == 0)
            {
                logger.LogInformation("Clipboard is empty.");
                return;
            }

            if (formats.Contains(DataFormats.Files))
            {
                var files = await Clipboard.GetDataAsync(DataFormats.Files);
                if (files is IEnumerable enumerable)
                {
                    foreach (var storageItem in enumerable.OfType<IStorageItem>())
                    {
                        var uri = storageItem.Path;
                        if (!uri.IsFile) break;
                        await AddFileUncheckAsync(uri.AbsolutePath);
                        if (chatAttachments.Count >= Settings.Internal.MaxChatAttachmentCount) break;
                    }
                }
            }
            else if (Settings.Model.IsImageSupported)
            {
                if (await nativeHelper.GetClipboardBitmapAsync() is not { } bitmap) return;

                chatAttachments.Add(new ChatImageAttachment(DynamicResourceKey.Empty, await ResizeImageOnDemandAsync(bitmap)));
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
        logger.ToExceptionHandler());

    private async static ValueTask<Bitmap> ResizeImageOnDemandAsync(Bitmap image, int maxWidth = 2560, int maxHeight = 2560)
    {
        if (image.PixelSize.Width <= maxWidth && image.PixelSize.Height <= maxHeight)
        {
            return image;
        }

        var scale = Math.Min(maxWidth / (double)image.PixelSize.Width, maxHeight / (double)image.PixelSize.Height);
        var newWidth = (int)(image.PixelSize.Width * scale);
        var newHeight = (int)(image.PixelSize.Height * scale);

        return await Task.Run(() => image.CreateScaledBitmap(new PixelSize(newWidth, newHeight)));
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task AddFileAsync()
    {
        if (chatAttachments.Count >= Settings.Internal.MaxChatAttachmentCount) return;

        IReadOnlyList<IStorageFile> files;
        IsOpened = false;
        try
        {
            files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Images")
                        {
                            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"]
                        },
                        new FilePickerFileType("All Files")
                        {
                            Patterns = ["*"]
                        }
                    ]
                });
        }
        finally
        {
            IsOpened = true;
        }

        if (files.Count <= 0) return;
        if (files[0].TryGetLocalPath() is not { } filePath)
        {
            logger.LogInformation("File path is not available.");
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
        if (!File.Exists(filePath))
        {
            logger.LogInformation("File not found: {FilePath}", filePath);
            return;
        }

        var ext = Path.GetExtension(filePath).ToLower();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
        {
            if (Settings.Model.IsImageSupported)
            {
                try
                {
                    var bitmap = await Task.Run(() => new Bitmap(filePath));
                    chatAttachments.Add(
                        new ChatImageAttachment(
                            new DirectResourceKey(Path.GetFileName(filePath)),
                            await ResizeImageOnDemandAsync(bitmap)));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load image from file: {FilePath}", filePath);
                }
            }
        }

        // TODO: 0.3.0
        // chatAttachments.Add(new ChatFileAttachment(new DirectResourceKey(Path.GetFileName(filePath)), filePath));
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

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task SendMessage(string message) => ExecuteBusyTaskAsync(
        async cancellationToken =>
        {
            message = message.Trim();
            if (message.Length == 0) return;

            var userMessage = new UserChatMessage(message, chatAttachments.AsValueEnumerable().ToImmutableArray())
            {
                Inlines = { message }
            };
            chatAttachments.Clear();

            await ChatService.SendMessageAsync(userMessage, cancellationToken);

            if (Settings.Behavior.AutoPin == Enums.AutoPinBehavior.AfterAssistantResponded)
            {
                IsPined = true;
            }
        },
        logger.ToExceptionHandler(),
        cancellationToken: cancellationTokenSource.Token);

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RetryAsync(ChatMessageNode chatMessageNode) => ExecuteBusyTaskAsync(
        cancellationToken => ChatService.RetryAsync(chatMessageNode, cancellationToken),
        logger.ToExceptionHandler(),
        cancellationToken: cancellationTokenSource.Token);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        cancellationTokenSource.Cancel();
    }

    [RelayCommand]
    private Task CopyAsync(ChatMessage chatMessage) => Clipboard.SetTextAsync(chatMessage.ToString());

    [RelayCommand]
    private void Close()
    {
        IsOpened = false;
    }

    private void Reset()
    {
        cancellationTokenSource.Cancel();
        TargetBoundingRect = default;
        QuickActions = [];
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsBusy))
        {
            AddElementCommand.NotifyCanExecuteChanged();
            AddClipboardCommand.NotifyCanExecuteChanged();
            AddFileCommand.NotifyCanExecuteChanged();
            SendMessageCommand.NotifyCanExecuteChanged();
            RetryCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }
}