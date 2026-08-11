using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// Renders a compact circular context-usage indicator without introducing another UI dependency.
/// </summary>
public sealed class ContextUsageRing : Control, ICustomHitTest
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ContextUsageRing, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<ContextUsageRing, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ContextUsageRing, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<ContextUsageRing, IBrush?>(nameof(IndicatorBrush));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<ContextUsageRing, double>(nameof(Size));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<ContextUsageRing, double>(nameof(StrokeThickness), 2d);

    public static readonly StyledProperty<FlyoutBase?> FlyoutProperty =
        AvaloniaProperty.Register<ContextUsageRing, FlyoutBase?>(nameof(Flyout));

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? IndicatorBrush
    {
        get => GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public FlyoutBase? Flyout
    {
        get => GetValue(FlyoutProperty);
        set => SetValue(FlyoutProperty, value);
    }

    private TopLevel? _topLevel;
    private bool _animationStarted;
    private TimeSpan? _lastFrameTime;
    private double _animationAngle;
    private IDisposable? _flyoutShowTimer;

    static ContextUsageRing()
    {
        AffectsRender<ContextUsageRing>(
            ValueProperty,
            IsIndeterminateProperty,
            TrackBrushProperty,
            IndicatorBrushProperty,
            StrokeThicknessProperty);
    }

    public ContextUsageRing()
    {
        this[!WidthProperty] = this[!SizeProperty];
        this[!HeightProperty] = this[!SizeProperty];
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsIndeterminateProperty || change.Property == IsVisibleProperty) StartAnimation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        StartAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel = null;
        _animationStarted = false;
        _lastFrameTime = null;

        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);

        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
        if (Flyout is not null)
        {
            _flyoutShowTimer = DispatcherTimer.RunOnce(() =>
            {
                if (Flyout is not null && IsPointerOver)
                {
                    Flyout.ShowAt(this);
                }
            }, TimeSpan.FromMilliseconds(500));
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        DisposeHelper.DisposeToDefault(ref _flyoutShowTimer);
        Flyout?.ShowAt(this);
    }

    public bool HitTest(Point point) => Bounds.Contains(point);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        var thickness = Math.Max(0d, StrokeThickness);
        var radius = Math.Max(0d, Math.Min(bounds.Width, bounds.Height) / 2d - thickness / 2d);
        if (radius <= 0d || thickness <= 0d) return;

        var center = new Point(bounds.Width / 2d, bounds.Height / 2d);
        context.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0d, 1d);
        var isIndeterminate = IsIndeterminate;
        if (!isIndeterminate && value <= 0d) return;
        if (!isIndeterminate && value >= 0.999d)
        {
            context.DrawEllipse(null, new Pen(IndicatorBrush, thickness), center, radius, radius);
            return;
        }

        var startAngle = isIndeterminate ? _animationAngle : -90d;
        var sweepAngle = isIndeterminate ? 100d : 360d * value;
        const int segmentCount = 48;
        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(GetPoint(startAngle), false);
            for (var i = 1; i <= segmentCount; i++)
            {
                geometryContext.LineTo(GetPoint(startAngle + sweepAngle * i / segmentCount));
            }
        }

        context.DrawGeometry(null, new Pen(IndicatorBrush, thickness), geometry);
        return;

        Point GetPoint(double angle)
        {
            var radians = angle * Math.PI / 180d;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
    }

    private void StartAnimation()
    {
        if (_animationStarted || _topLevel is null || !IsVisible || !IsIndeterminate) return;

        _animationStarted = true;
        _lastFrameTime = null;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        if (_topLevel is null || !IsVisible || !IsIndeterminate)
        {
            _animationStarted = false;
            _lastFrameTime = null;
            return;
        }

        if (_lastFrameTime is { } lastFrameTime)
        {
            _animationAngle = (_animationAngle + (time - lastFrameTime).TotalSeconds * 270d) % 360d;
        }

        _lastFrameTime = time;
        InvalidateVisual();
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }
}