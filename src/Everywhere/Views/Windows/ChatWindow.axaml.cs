using System.ComponentModel;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.Logging;
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

    public static readonly StyledProperty<PixelRect> TargetBoundingRectProperty =
        AvaloniaProperty.Register<ChatWindow, PixelRect>(nameof(TargetBoundingRect));

    public PixelRect TargetBoundingRect
    {
        get => GetValue(TargetBoundingRectProperty);
        set => SetValue(TargetBoundingRectProperty, value);
    }

    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        AvaloniaProperty.Register<ChatWindow, PlacementMode>(nameof(Placement));

    public PlacementMode Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    public static readonly StyledProperty<bool> IsWindowPinnedProperty =
        AvaloniaProperty.Register<ChatWindow, bool>(nameof(IsWindowPinned));

    public bool IsWindowPinned
    {
        get => GetValue(IsWindowPinnedProperty);
        set => SetValue(IsWindowPinnedProperty, value);
    }

    private readonly ILauncher _launcher;
    private readonly IWindowHelper _windowHelper;
    private readonly Settings _settings;

    public ChatWindow(
        ILauncher launcher,
        IChatContextManager chatContextManager,
        IWindowHelper windowHelper,
        Settings settings)
    {
        _launcher = launcher;
        _windowHelper = windowHelper;
        _settings = settings;

        InitializeComponent();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel, true);

        chatContextManager.PropertyChanged += HandleChatContextManagerPropertyChanged;
        ViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        ChatInputBox.TextChanged += HandleChatInputBoxTextChanged;
        ChatInputBox.PastingFromClipboard += HandleChatInputBoxPastingFromClipboard;
        
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
        ShowActivated = true;
        Topmost = true;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e)
        {
            case { Key: Key.Escape }:
            {
                IsOpened = false;
                e.Handled = true;
                break;
            }
            case { Key: Key.D, KeyModifiers: KeyModifiers.Control }:
            {
                IsWindowPinned = !IsWindowPinned;
                e.Handled = true;
                break;
            }
            case { Key: Key.N, KeyModifiers: KeyModifiers.Control }:
            {
                ViewModel.ChatContextManager.CreateNewCommand.Execute(null);
                e.Handled = true;
                break;
            }
            case { Key: Key.T, KeyModifiers: KeyModifiers.Control } when
                _settings.Model.SelectedCustomAssistant?.IsFunctionCallingSupported.ActualValue is true:
            {
                _settings.Internal.IsToolCallEnabled = !_settings.Internal.IsToolCallEnabled;
                e.Handled = true;
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
        else if (change.Property == TargetBoundingRectProperty)
        {
            CalculatePositionAndPlacement();
        }
        else if (change.Property == IsWindowPinnedProperty)
        {
            var value = change.NewValue is true;
            _settings.Internal.IsChatWindowPinned = value;
            ShowInTaskbar = value;
            _windowHelper.SetCloaked(this, false); // Uncloak when pinned state changes to ensure visibility
        }
    }

    /// <summary>
    /// Indicates whether the window has been resized by the user.
    /// </summary>
    private bool _isResizedByUser;

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        if (e.Reason == WindowResizeReason.User)
        {
            _isResizedByUser = true;
        }
        else if (!_isResizedByUser)
        {
            ClampToScreen();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_isResizedByUser)
        {
            availableSize = new Size(400d, 600d);
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

        if (_isResizedByUser)
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

        if (!ViewModel.IsPickingFiles && !IsActive && !IsWindowPinned && !_windowHelper.AnyModelDialogOpened(this))
        {
            IsOpened = false;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return null!; // Disable automation peer to avoid being detected by self
    }

    private void CalculatePositionAndPlacement()
    {
        // 1. Get the available area of all screens
        var actualSize = Bounds.Size.To(s => new PixelSize((int)(s.Width * DesktopScaling), (int)(s.Height * DesktopScaling)));
        if (actualSize == PixelSize.Empty)
        {
            // If the size is empty, we cannot calculate the position and placement
            return;
        }

        // 2. Screen coordinates and this window size of the target element
        var targetBoundingRectangle = TargetBoundingRect;
        if (targetBoundingRectangle.Width <= 0 || targetBoundingRectangle.Height <= 0)
        {
            // If the target bounding rectangle is invalid, we cannot calculate the position and placement
            return;
        }

        // 3. Generate a candidate list based on the priority of attachment (right → bottom → top → left) and alignment priority (top/left priority)
        var candidates = new List<(PlacementMode mode, PixelPoint pos)>
        {
            // →
            (PlacementMode.RightEdgeAlignedTop, new PixelPoint(targetBoundingRectangle.X + targetBoundingRectangle.Width, targetBoundingRectangle.Y)),
            (PlacementMode.RightEdgeAlignedBottom,
                new PixelPoint(
                    targetBoundingRectangle.X + targetBoundingRectangle.Width,
                    targetBoundingRectangle.Y + targetBoundingRectangle.Height - actualSize.Height)),

            // ↓
            (PlacementMode.BottomEdgeAlignedLeft,
                new PixelPoint(targetBoundingRectangle.X, targetBoundingRectangle.Y + targetBoundingRectangle.Height)),
            (PlacementMode.BottomEdgeAlignedRight,
                new PixelPoint(
                    targetBoundingRectangle.X + targetBoundingRectangle.Width - actualSize.Width,
                    targetBoundingRectangle.Y + targetBoundingRectangle.Height)),

            // ↑
            (PlacementMode.TopEdgeAlignedLeft, new PixelPoint(targetBoundingRectangle.X, targetBoundingRectangle.Y - actualSize.Height)),
            (PlacementMode.TopEdgeAlignedRight,
                new PixelPoint(
                    targetBoundingRectangle.X + targetBoundingRectangle.Width - actualSize.Width,
                    targetBoundingRectangle.Y - actualSize.Height)),

            // ←
            (PlacementMode.LeftEdgeAlignedTop, new PixelPoint(targetBoundingRectangle.X - actualSize.Width, targetBoundingRectangle.Y)),
            (PlacementMode.LeftEdgeAlignedBottom,
                new PixelPoint(
                    targetBoundingRectangle.X - actualSize.Width,
                    targetBoundingRectangle.Y + targetBoundingRectangle.Height - actualSize.Height)),

            // center
            (PlacementMode.Center,
                new PixelPoint(
                    targetBoundingRectangle.X + targetBoundingRectangle.Width / 2 - actualSize.Width / 2,
                    targetBoundingRectangle.Y + targetBoundingRectangle.Height / 2 - actualSize.Height / 2))
        };

        // 4. Search for the first candidate that completely falls into any screen workspace
        var screenAreas = Screens.All.Select(s => s.Bounds).ToReadOnlyList();
        foreach (var (mode, pos) in candidates)
        {
            var rect = new PixelRect(pos, actualSize);
            if (screenAreas.Any(area => area.Contains(rect)))
            {
                Position = pos;
                Placement = mode;
                return;
            }
        }

        // 5. If none of them are met, use the preferred solution and clamp it onto the main screen
        var (fallbackMode, fallbackPos) = candidates[0];
        var mainArea = screenAreas[0];
        Position = ClampToArea(fallbackPos, actualSize, mainArea);
        Placement = fallbackMode;
    }

    private void ClampToScreen()
    {
        var position = Position;
        var actualSize = Bounds.Size.To(s => new PixelSize((int)(s.Width * DesktopScaling), (int)(s.Height * DesktopScaling)));
        var screenBounds = Screens.ScreenFromPoint(position)?.Bounds ?? Screens.Primary?.Bounds ?? Screens.All[0].Bounds;
        Position = ClampToArea(position, actualSize, screenBounds);
    }

    private static PixelPoint ClampToArea(PixelPoint pos, PixelSize size, PixelRect area)
    {
        var x = Math.Max(area.X, Math.Min(pos.X, area.X + area.Width - size.Width));
        var y = Math.Max(area.Y, Math.Min(pos.Y, area.Y + area.Height - size.Height));
        return new PixelPoint(x, y);
    }
    
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TitleBarBorder.Bounds.Contains(e.GetCurrentPoint(this).Position))
            BeginMoveDrag(e);
    }

    private void HandleChatContextManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IChatContextManager.Current)) return;
        Dispatcher.UIThread.InvokeOnDemand(() => SizeToContent = SizeToContent.WidthAndHeight); // Update size to content when chat context changes
        _isResizedByUser = false;
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
                    IsWindowPinned = _settings.Internal.IsChatWindowPinned;
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

            ShowInTaskbar = IsWindowPinned;
            _windowHelper.SetCloaked(this, false);
            ChatInputBox.Focus();
        }
        else
        {
            ShowInTaskbar = false;
            _windowHelper.SetCloaked(this, true);
        }
    }

    private void HandleChatInputBoxTextChanged(object? sender, TextChangedEventArgs e)
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
    private void HandleChatInputBoxPastingFromClipboard(object? sender, RoutedEventArgs e)
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

        ServiceLocator.Resolve<ILogger<ChatWindow>>().LogError("Chat window was closed unexpectedly. This should not happen.");
    }

    [RelayCommand]
    private Task LaunchInlineHyperlink(InlineHyperlinkClickedEventArgs e)
    {
        // currently we only support http(s) links for safety reasons
        return e.HRef is not { Scheme: "http" or "https" } uri ? Task.CompletedTask : _launcher.LaunchUriAsync(uri);
    }

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        UpdateDragDropOverlay(e, true);
        e.Handled = true;
    }

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File) || ViewModel.IsBusy)
        {
            e.DragEffects = DragDropEffects.None;
        }
        else
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        
        e.Handled = true;
    }

    private void HandleDragLeave(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private async void HandleDrop(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;

        if (ViewModel.IsBusy)
        {
            e.Handled = true;
            return;
        }

        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.Handled = true;
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files == null)
        {
            e.Handled = true;
            return;
        }

        var fileList = files.ToList();
        foreach (var storageItem in fileList)
        {
            var uri = storageItem.Path;
            if (!uri.IsFile) continue;

            if (storageItem.TryGetLocalPath() is not { } localPath) continue;
            
            try
            {
                await ViewModel.AddFileFromDragDropAsync(localPath);
            }
            catch (Exception ex)
            {
                ServiceLocator.Resolve<ILogger<ChatWindow>>()
                    .LogError(ex, "Failed to add dropped file: {FilePath}", localPath);
            }

            if (ViewModel.ChatAttachments.Count >= _settings.Internal.MaxChatAttachmentCount)
            {
                break;
            }
        }

        e.Handled = true;
    }

    private void UpdateDragDropOverlay(DragEventArgs e, bool show)
    {
        if (!show)
        {
            DragDropOverlay.IsVisible = false;
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files != null)
        {
            var fileList = files.ToList();
            var firstFile = fileList.FirstOrDefault();
            
            if (firstFile?.Path.AbsolutePath is { } path)
            {
                var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                
                if (IsImageExtension(extension))
                {
                    DragDropIcon.Kind = Lucide.Avalonia.LucideIconKind.Image;
                }
                else
                {
                    DragDropIcon.Kind = Lucide.Avalonia.LucideIconKind.FileUp;
                }
            }
        }

        DragDropOverlay.IsVisible = true;
    }

    private static bool IsImageExtension(string extension)
    {
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg";
    }

}