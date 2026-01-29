using System.ComponentModel;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AttachedProperties;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Serilog;
using ShadUI;

namespace Everywhere.Views;

public partial class ChatWindow : ReactiveShadWindow<ChatWindowViewModel>, IReactiveHost
{
    public DialogHost DialogHost => PART_DialogHost;

    public ToastHost ToastHost => PART_ToastHost;

    public static readonly DirectProperty<ChatWindow, bool> IsOpenedProperty =
        AvaloniaProperty.RegisterDirect<ChatWindow, bool>(nameof(IsOpened), o => o.IsOpened);

    public bool IsOpened
    {
        get;
        private set => SetAndRaise(IsOpenedProperty, ref field, value);
    }

    /// <summary>
    /// Defines the <see cref="IsWindowPinned"/> property.
    /// </summary>
    public static readonly StyledProperty<bool?> IsWindowPinnedProperty =
        AvaloniaProperty.Register<ChatWindow, bool?>(nameof(IsWindowPinned));

    /// <summary>
    /// Gets or sets a value indicating whether the window is pinned.
    /// true: pinned and on top
    /// null: pinned but not on top
    /// false: not pinned, on top. hidden when unfocused
    /// </summary>
    public bool? IsWindowPinned
    {
        get => GetValue(IsWindowPinnedProperty);
        set => SetValue(IsWindowPinnedProperty, value);
    }

    private static Size DefaultSize => new(400d, 600d);

    private readonly ILauncher _launcher;
    private readonly IWindowHelper _windowHelper;
    private readonly Settings _settings;
    private readonly PersistentState _persistentState;

    /// <summary>
    /// Indicates whether the window has been resized by the user.
    /// </summary>
    private bool _isUserResized;

    /// <summary>
    /// Indicates whether the window can be closed.
    /// </summary>
    private bool _canCloseWindow;

    public ChatWindow(
        ILauncher launcher,
        IWindowHelper windowHelper,
        Settings settings,
        PersistentState persistentState)
    {
        _launcher = launcher;
        _windowHelper = windowHelper;
        _settings = settings;
        _persistentState = persistentState;

        InitializeComponent();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel, true);

        ViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        ChatInputArea.TextChanged += HandleChatInputAreaTextChanged;
        ChatInputArea.PastingFromClipboard += HandleChatInputAreaPastingFromClipboard;

        SetupDragDropHandlers();
    }

    private void SetupDragDropHandlers()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, HandleDragEnter);
        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DragLeaveEvent, HandleDragLeave);
        AddHandler(DragDrop.DropEvent, HandleDrop);
    }

    /// <summary>
    /// Initializes the chat window.
    /// </summary>
    public void Initialize()
    {
        EnsureInitialized();
        ApplyStyling();
        ApplyTemplate();

        _windowHelper.SetCloaked(this, true);
        Topmost = true;

        // Setup window placement saving after initialization
        this[SaveWindowPlacementAssist.KeyProperty] = nameof(ChatWindow);
        _isUserResized = SizeToContent != SizeToContent.WidthAndHeight;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e)
        {
            case { Key: Key.Escape }:
            {
                if (ViewModel.EditingUserMessageNode is not null)
                {
                    ViewModel.CancelEditing();
                }
                else
                {
                    IsOpened = false;
                }

                e.Handled = true;
                break;
            }
            case { Key: Key.H, KeyModifiers: KeyModifiers.Control }:
            {
                var newValue = !ViewModel.IsViewingHistory;
                if (ViewModel.SwitchViewingHistoryCommand.CanExecute(newValue))
                {
                    ViewModel.SwitchViewingHistoryCommand.Execute(newValue);
                    e.Handled = true;
                }
                break;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty)
        {
            IsOpened = change.NewValue is true;
        }
        else if (change.Property == IsWindowPinnedProperty)
        {
            var value = change.NewValue as bool?;
            _persistentState.IsChatWindowPinned = value;
            ShowInTaskbar = value is true or null;
            Topmost = value is not null; // false: topmost, null: normal, true: topmost
            _windowHelper.SetCloaked(this, false); // Uncloak when pinned state changes to ensure visibility
        }
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        if (e.Reason != WindowResizeReason.User) return;

        if (e.ClientSize.NearlyEquals(new Size(MinWidth, MinHeight)))
        {
            _isUserResized = false;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            _isUserResized = true;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_isUserResized)
        {
            availableSize = DefaultSize;
        }

        double width = 0;
        double height = 0;

        {
            var visualCount = VisualChildren.Count;
            for (var i = 0; i < visualCount; i++)
            {
                var visual = VisualChildren[i];
                if (visual is not Layoutable layoutable) continue;

                layoutable.Measure(availableSize);
                var childSize = layoutable.DesiredSize;
                if (childSize.Width > width) width = childSize.Width;
                if (childSize.Height > height) height = childSize.Height;
            }
        }

        if (_isUserResized)
        {
            var clientSize = ClientSize;

            if (!double.IsInfinity(availableSize.Width))
            {
                width = availableSize.Width;
            }
            else
            {
                width = clientSize.Width;
            }

            if (!double.IsInfinity(availableSize.Height))
            {
                height = availableSize.Height;
            }
            else
            {
                height = clientSize.Height;
            }
        }

        return new Size(width, height);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        if (!ViewModel.IsPickingFiles && !IsActive && IsWindowPinned is false && !_windowHelper.AnyModelDialogOpened(this))
        {
            IsOpened = false;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new NoneAutomationPeer(this); // Disable automation peer to avoid being detected by self
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TitleBarBorder.Bounds.Contains(e.GetCurrentPoint(this).Position))
            BeginMoveDrag(e);
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ViewModel.IsOpened)) return;

        IsOpened = ViewModel.IsOpened;
        if (IsOpened)
        {
            switch (_settings.ChatWindow.WindowPinMode)
            {
                case ChatWindowPinMode.RememberLast:
                {
                    IsWindowPinned = _persistentState.IsChatWindowPinned;
                    break;
                }
                case ChatWindowPinMode.AlwaysPinned:
                {
                    IsWindowPinned = true;
                    break;
                }
                case ChatWindowPinMode.AlwaysUnpinned:
                case ChatWindowPinMode.PinOnInput:
                {
                    IsWindowPinned = false;
                    break;
                }
            }

            ShowInTaskbar = IsWindowPinned is true or null;
            _windowHelper.SetCloaked(this, false);
            ChatInputArea.Focus();
        }
        else
        {
            ShowInTaskbar = false;
            _windowHelper.SetCloaked(this, true);
        }
    }

    private void HandleChatInputAreaTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_settings.ChatWindow.WindowPinMode == ChatWindowPinMode.PinOnInput)
        {
            IsWindowPinned = true;
        }
    }

    /// <summary>
    /// TODO: Avalonia says they will support this in 12.0
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void HandleChatInputAreaPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.AddClipboardCommand.CanExecute(null)) return;

        ViewModel.AddClipboardCommand.Execute(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // allow closing only on application or OS shutdown
        // otherwise, Windows will say "Everywhere is preventing shutdown"
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            _canCloseWindow = true;
            base.OnClosing(e);
            return;
        }

        // do not allow closing, just hide the window
        e.Cancel = true;
        IsOpened = false;

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (!_canCloseWindow)
            Log.ForContext<ChatWindow>().Error("Chat window was closed unexpectedly. This should not happen.");
    }

    [RelayCommand]
    private Task LaunchLink(LinkClickedEventArgs e)
    {
        // currently we only support http(s) links for safety reasons
        return e.HRef is not { Scheme: "http" or "https" } uri ? Task.CompletedTask : _launcher.LaunchUriAsync(uri);
    }

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragVisuals(e);
        e.Handled = true;
    }

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragVisuals(e);
        e.Handled = true;
    }

    private void UpdateDragVisuals(DragEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            e.DragEffects = DragDropEffects.None;
            DragDropOverlay.IsVisible = false;
            return;
        }

        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        var hasText = e.DataTransfer.Contains(DataFormat.Text);

        if (!hasFiles && !hasText)
        {
            e.DragEffects = DragDropEffects.None;
            DragDropOverlay.IsVisible = false;
            return;
        }

        // Check file support
        if (hasFiles)
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                var hasSupportedFile = false;
                var hasUnsupportedFile = false;
                string? firstMimeType = null;

                foreach (var item in files)
                {
                    if (IsSupportedFile(item, out _, out var mimeType))
                    {
                        hasSupportedFile = true;
                        firstMimeType ??= mimeType;
                    }
                    else
                    {
                        hasUnsupportedFile = true;
                    }
                }

                if (hasUnsupportedFile)
                {
                    e.DragEffects = DragDropEffects.None;
                    DragDropIcon.Kind = LucideIconKind.FileX;
                    DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_Unsupported;
                    DragDropOverlay.IsVisible = true;
                    return;
                }

                if (hasSupportedFile)
                {
                    if (ViewModel.ChatAttachments.Count >= _persistentState.MaxChatAttachmentCount)
                    {
                        e.DragEffects = DragDropEffects.None;
                        DragDropOverlay.IsVisible = false;
                        return;
                    }

                    e.DragEffects = DragDropEffects.Copy;
                    DragDropIcon.Kind = firstMimeType != null && FileUtilities.IsOfCategory(firstMimeType, FileTypeCategory.Image) ?
                        LucideIconKind.Image :
                        LucideIconKind.FileUp;
                    DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_DropFilesHere;
                    DragDropOverlay.IsVisible = true;
                    return;
                }
            }
        }

        // Text only
        if (hasText)
        {
            e.DragEffects = DragDropEffects.Copy;
            DragDropIcon.Kind = LucideIconKind.TextCursorInput;
            DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_DropTextHere;
            DragDropOverlay.IsVisible = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
        DragDropOverlay.IsVisible = false;
    }

    private void HandleDragLeave(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void HandleDrop(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;

        if (ViewModel.IsBusy)
            return;

        HandleDropAsync().Detach(ToastHost.Manager.ToExceptionHandler());

        async Task HandleDropAsync()
        {
            // Handle file drops
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                var files = e.DataTransfer.TryGetFiles();
                if (files is null) return;

                foreach (var item in files)
                {
                    if (!IsSupportedFile(item, out var localPath, out _))
                        continue;

                    try
                    {
                        await ViewModel.AddFileFromDragDropAsync(localPath!);
                    }
                    catch (Exception ex)
                    {
                        ServiceLocator.Resolve<ILogger<ChatWindow>>()
                            .LogError(ex, "Failed to add dropped file: {FilePath}", localPath);
                    }

                    if (ViewModel.ChatAttachments.Count >= _persistentState.MaxChatAttachmentCount) break;
                }
            }


            // Handle text drops
            if (e.DataTransfer.Contains(DataFormat.Text))
            {
                var text = e.DataTransfer.TryGetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                var currentText = _persistentState.ChatInputAreaText ?? string.Empty;
                var caretIndex = ChatInputArea.CaretIndex;
                _persistentState.ChatInputAreaText = currentText.Insert(caretIndex, text);
                ChatInputArea.CaretIndex = caretIndex + text.Length;
            }
        }
    }

    private static bool IsSupportedFile(IStorageItem storageItem, out string? localPath, out string? mimeType)
    {
        localPath = null;
        mimeType = null;

        if (!storageItem.Path.IsFile || storageItem.TryGetLocalPath() is not { } path)
            return false;

        localPath = path;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (FileUtilities.KnownMimeTypes.TryGetValue(extension, out var mime) &&
            FileUtilities.KnownFileTypes.TryGetValue(mime, out var fileType) &&
            fileType is FileTypeCategory.Image or FileTypeCategory.Audio or FileTypeCategory.Document or FileTypeCategory.Script)
        {
            mimeType = mime;
            return true;
        }

        return false;
    }

}