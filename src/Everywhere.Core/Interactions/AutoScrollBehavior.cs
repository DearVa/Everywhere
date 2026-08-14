using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

public enum AutoScrollBehaviorMode
{
    None,
    Always,
    WhenAtEnd
}

public class AutoScrollBehavior : Behavior<ScrollViewer>
{
    private const double EndTolerance = 0.5;

    public static readonly StyledProperty<AutoScrollBehaviorMode> ModeProperty =
        AvaloniaProperty.Register<AutoScrollBehavior, AutoScrollBehaviorMode>(nameof(Mode), AutoScrollBehaviorMode.WhenAtEnd);

    public static readonly StyledProperty<object?> ScrollToEndTokenProperty =
        AvaloniaProperty.Register<AutoScrollBehavior, object?>(nameof(ScrollToEndToken));

    public AutoScrollBehaviorMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public object? ScrollToEndToken
    {
        get => GetValue(ScrollToEndTokenProperty);
        set => SetValue(ScrollToEndTokenProperty, value);
    }

    private bool _isAtEnd = true;
    private bool _isScrollToEndPending;
    private long _scrollToEndRequestVersion;

    static AutoScrollBehavior()
    {
        ScrollToEndTokenProperty.Changed.AddClassHandler<AutoScrollBehavior>((behavior, _) =>
            behavior.RequestScrollToEnd());
    }

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject is not null)
        {
            AssociatedObject.PropertyChanged += OnScrollViewerPropertyChanged;
        }

        if (_isScrollToEndPending)
            RequestScrollToEnd();
    }

    protected override void OnDetaching()
    {
        _scrollToEndRequestVersion++;
        _isScrollToEndPending = false;

        if (AssociatedObject is not null)
            AssociatedObject.PropertyChanged -= OnScrollViewerPropertyChanged;

        base.OnDetaching();
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (AssociatedObject is not { } scrollViewer)
            return;

        if (e.Property != ScrollViewer.OffsetProperty &&
            e.Property != ScrollViewer.ViewportProperty &&
            e.Property != ScrollViewer.ExtentProperty)
        {
            return;
        }

        if (e.Property == ScrollViewer.OffsetProperty && !_isScrollToEndPending)
        {
            var oldOffset = e.OldValue.To<Vector>().Y;
            var newOffset = e.NewValue.To<Vector>().Y;
            var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);

            if (newOffset >= maximumOffset - EndTolerance)
                _isAtEnd = true;
            else if (newOffset < oldOffset - EndTolerance)
                _isAtEnd = false;
        }

        if (Mode == AutoScrollBehaviorMode.Always || Mode == AutoScrollBehaviorMode.WhenAtEnd && _isAtEnd)
            ScrollToEnd(scrollViewer);
    }

    private void RequestScrollToEnd()
    {
        _isAtEnd = true;
        _isScrollToEndPending = true;
        var version = ++_scrollToEndRequestVersion;

        Dispatcher.UIThread.Post(
            () =>
            {
                if (version != _scrollToEndRequestVersion || AssociatedObject is not { } scrollViewer)
                    return;

                _isScrollToEndPending = false;
                if (Mode != AutoScrollBehaviorMode.None)
                    ScrollToEnd(scrollViewer);
            },
            DispatcherPriority.Loaded);
    }

    private static void ScrollToEnd(ScrollViewer scrollViewer) =>
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, double.PositiveInfinity);
}