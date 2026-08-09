using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using Everywhere.Views;

namespace Everywhere.Interactions;

/// <summary>
/// Performs the view-only half of chat search navigation: first realizing a stable row through the
/// virtualizing panel, then centering the exact text range when its search surface becomes ready.
/// </summary>
public sealed class ChatTextSearchNavigationBehavior : Behavior<ChatMessageItemsControl>
{
    public static readonly StyledProperty<ChatTextSearchViewModel?> CoordinatorProperty =
        AvaloniaProperty.Register<ChatTextSearchNavigationBehavior, ChatTextSearchViewModel?>(nameof(Coordinator));

    public static readonly StyledProperty<ScrollViewer?> ScrollViewerProperty =
        AvaloniaProperty.Register<ChatTextSearchNavigationBehavior, ScrollViewer?>(nameof(ScrollViewer));

    public ChatTextSearchViewModel? Coordinator
    {
        get => GetValue(CoordinatorProperty);
        set => SetValue(CoordinatorProperty, value);
    }

    public ScrollViewer? ScrollViewer
    {
        get => GetValue(ScrollViewerProperty);
        set => SetValue(ScrollViewerProperty, value);
    }

    private ChatTextSearchViewModel? _subscribedCoordinator;
    private bool _navigationQueued;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject?.TextSearchSurfaceRegistry.SurfaceChanged += HandleSurfaceChanged;
        ReconnectCoordinator();
    }

    protected override void OnDetaching()
    {
        AssociatedObject?.TextSearchSurfaceRegistry.SurfaceChanged -= HandleSurfaceChanged;
        DisconnectCoordinator();
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoordinatorProperty)
        {
            ReconnectCoordinator();
        }
    }

    private void ReconnectCoordinator()
    {
        DisconnectCoordinator();
        if (Coordinator is not { } coordinator) return;

        _subscribedCoordinator = coordinator;
        coordinator.NavigationRequested += HandleNavigationRequested;
    }

    private void DisconnectCoordinator()
    {
        if (_subscribedCoordinator is null) return;
        _subscribedCoordinator.NavigationRequested -= HandleNavigationRequested;
        _subscribedCoordinator = null;
    }

    private void HandleNavigationRequested(object? sender, EventArgs e) => QueueNavigation();

    private void HandleSurfaceChanged(ChatPresentationRow row)
    {
        if (Coordinator?.GetCurrentMatch() is { } match && ReferenceEquals(match.Row, row))
        {
            QueueNavigation();
        }
    }

    private void QueueNavigation()
    {
        if (_navigationQueued) return;
        _navigationQueued = true;
        Dispatcher.UIThread.Post(Navigate, DispatcherPriority.Loaded);
    }

    private void Navigate()
    {
        _navigationQueued = false;
        if (AssociatedObject is not { } itemsControl || ScrollViewer is not { } scrollViewer || Coordinator?.GetCurrentMatch() is not { } match)
        {
            return;
        }

        itemsControl.ScrollIntoView(match.Row);
        if (!itemsControl.TextSearchSurfaceRegistry.TryGet(match.Row, out var surface) ||
            !surface.TryGetMatchCenter(match.LocalIndex, scrollViewer, out var center))
        {
            return;
        }

        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var targetOffset = scrollViewer.Offset.Y + center.Y - scrollViewer.Viewport.Height / 2;
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(targetOffset, 0, maximumOffset));
    }
}