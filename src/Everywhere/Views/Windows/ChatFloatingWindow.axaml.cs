using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using LiveMarkdown.Avalonia;

namespace Everywhere.Views;

public partial class ChatFloatingWindow : ReactiveWindow<ChatFloatingWindowViewModel>
{
    public static readonly DirectProperty<ChatFloatingWindow, bool> IsOpenedProperty =
        AvaloniaProperty.RegisterDirect<ChatFloatingWindow, bool>(nameof(IsOpened), o => o.IsOpened);

    public bool IsOpened
    {
        get;
        private set => SetAndRaise(IsOpenedProperty, ref field, value);
    }

    public static readonly StyledProperty<PixelRect> TargetBoundingRectProperty =
        AvaloniaProperty.Register<ChatFloatingWindow, PixelRect>(nameof(TargetBoundingRect));

    public PixelRect TargetBoundingRect
    {
        get => GetValue(TargetBoundingRectProperty);
        set => SetValue(TargetBoundingRectProperty, value);
    }

    public static readonly StyledProperty<PlacementMode> PlacementProperty =
        AvaloniaProperty.Register<ChatFloatingWindow, PlacementMode>(nameof(Placement));

    public PlacementMode Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    private readonly ILauncher launcher;

    public ChatFloatingWindow(ILauncher launcher)
    {
        this.launcher = launcher;

        InitializeComponent();

        ViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        ChatInputBox.PastingFromClipboard += HandlePastingFromClipboard;
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ViewModel.IsOpened)) return;

        IsOpened = ViewModel.IsOpened;
        if (IsOpened)
        {
            Show();
            Topmost = false;
            Topmost = true;
            ChatInputBox.Focus();
        }
        else
        {
            Hide();
        }
    }

    public override void Show()
    {
        ViewModel.IsPined = false;
        if (ViewModel.Settings.Behavior.AutoPin == Enums.AutoPinBehavior.Always)
        {
            ViewModel.IsPined = true;
        }

        base.Show();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        if (!IsActive &&
            !ViewModel.IsPined)
        {
            Hide();
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
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        ClampToScreen();
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

    private void HandleTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void HandleInputBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (ViewModel.Settings.Behavior.AutoPin == Enums.AutoPinBehavior.AfterInput)
        {
            ViewModel.IsPined = true;
        }
    }

    /// <summary>
    /// TODO: Avalonia says they will support this in 12.0
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void HandlePastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.AddClipboardCommand.CanExecute(null)) return;

        ViewModel.AddClipboardCommand.Execute(null);
    }

    [RelayCommand]
    private Task LaunchInlineHyperlink(InlineHyperlinkClickedEventArgs e)
    {
        // currently we only support http(s) links for safety reasons
        return e.HRef is not { Scheme: "http" or "https" } uri ? Task.CompletedTask : launcher.LaunchUriAsync(uri);
    }
}