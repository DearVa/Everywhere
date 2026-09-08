using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Everywhere.Chat;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// A drawn turn rail and a bounded preview overlay. Scrolling is a scalar offset, not a scroll
/// container; only the visible range plus two overscan marks is visited by the renderer.
/// </summary>
public sealed class ChatTurnNavigator : Decorator, ICustomHitTest
{
    /// <summary>
    /// Defines the full-area background reached while previewing a turn.
    /// </summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, IBrush?>(nameof(Background));

    /// <summary>
    /// Gets or sets the hover backdrop. Its opacity follows the preview entrance and exit.
    /// </summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// Defines the selected conversation.
    /// </summary>
    public static readonly StyledProperty<ChatContext?> ChatContextProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatContext?>(nameof(ChatContext));

    /// <summary>
    /// Defines the message list receiving explicit turn navigation.
    /// </summary>
    public static readonly StyledProperty<ChatMessageItemsControl?> TargetProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatMessageItemsControl?>(nameof(Target));

    /// <summary>
    /// Defines the user node currently intersecting the message viewport center.
    /// </summary>
    public static readonly StyledProperty<ChatMessageNode?> ReadingNodeProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, ChatMessageNode?>(nameof(ReadingNode));

    /// <summary>
    /// Gets or sets the selected conversation.
    /// </summary>
    public ChatContext? ChatContext
    {
        get => GetValue(ChatContextProperty);
        set => SetValue(ChatContextProperty, value);
    }

    /// <summary>
    /// Gets or sets the message list receiving explicit turn navigation.
    /// </summary>
    public ChatMessageItemsControl? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>
    /// Gets or sets the turn highlighted independently from pointer preview.
    /// </summary>
    public ChatMessageNode? ReadingNode
    {
        get => GetValue(ReadingNodeProperty);
        set => SetValue(ReadingNodeProperty, value);
    }

    /// <summary>
    /// Defines the theme line height used to budget preview slots before they are created.
    /// </summary>
    public static readonly StyledProperty<double> PreviewLineHeightProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, double>(nameof(PreviewLineHeight), 20);

    /// <summary>
    /// Gets or sets the theme line height used for preview capacity.
    /// </summary>
    public double PreviewLineHeight
    {
        get => GetValue(PreviewLineHeightProperty);
        set => SetValue(PreviewLineHeightProperty, value);
    }

    /// <summary>
    /// Defines the insets of the area available to the drawn rail, in DIPs.
    /// </summary>
    public static readonly StyledProperty<Thickness> LinePaddingProperty =
        AvaloniaProperty.Register<ChatTurnNavigator, Thickness>(
            nameof(LinePadding),
            new Thickness(0, 16),
            validate: value => double.IsFinite(value.Left) && double.IsFinite(value.Top) &&
                double.IsFinite(value.Right) && double.IsFinite(value.Bottom) &&
                value is { Left: >= 0, Top: >= 0, Right: >= 0, Bottom: >= 0 });

    /// <summary>
    /// Gets or sets rail padding without insetting previews or the backdrop.
    /// </summary>
    public Thickness LinePadding
    {
        get => GetValue(LinePaddingProperty);
        set => SetValue(LinePaddingProperty, value);
    }

    private const double Pitch = 12;
    private const double RailWidth = 40;
    private readonly Canvas _previews = new() { IsHitTestVisible = false };
    private readonly RectangleGeometry _previewClip = new();
    private readonly Dictionary<ChatTurnNavigationIndex.Entry, ChatTurnPreview> _cards = [];
    private readonly List<Wake> _wakes = [];
    private ChatTurnNavigationIndex? _index;
    private TopLevel? _topLevel;
    private TimeSpan? _lastFrame;
    private bool _isFramePending;
    private bool _isHovering;
    private int _attachmentVersion;
    private int _readingIndex = -1;
    private double _pointerY;
    private double _lastWakePosition = double.NaN;
    private Spring _focus;
    private Spring _offset;
    private Spring _presence;
    private Spring _previewY;

    private int TurnCount => _index?.Turns.Count ?? 0;
    private double RailAvailableHeight => Math.Max(0, Bounds.Height - LinePadding.Top - LinePadding.Bottom);
    private double RailHeight => Math.Min(400, RailAvailableHeight);
    private double MaximumOffset => Math.Max(0, (TurnCount - 1) * Pitch + 16 - RailHeight);
    private double Origin => RailClip.Top + Math.Max(8, (RailHeight - (TurnCount - 1) * Pitch) / 2);
    private double PointerPosition => Math.Clamp((_pointerY - Origin + _offset.Value) / Pitch, 0, Math.Max(0, TurnCount - 1));
    private double PreviewSlotHeight => PreviewLineHeight * 6 + 28;

    private Rect RailClip => new(
        Math.Min(LinePadding.Left, Bounds.Width),
        Math.Min(LinePadding.Top, Bounds.Height) + (RailAvailableHeight - RailHeight) / 2,
        Math.Min(RailWidth, Math.Max(0, Bounds.Width - LinePadding.Left - LinePadding.Right)),
        RailHeight);

    private Rect HitBounds
    {
        get
        {
            if (TurnCount == 0) return default;
            var firstY = Origin - _offset.Value;
            var lastY = firstY + (TurnCount - 1) * Pitch;
            var top = Math.Max(RailClip.Top, firstY - Pitch);
            var bottom = Math.Min(RailClip.Bottom, lastY + Pitch);
            return new Rect(RailClip.Left, top, RailClip.Width, Math.Max(0, bottom - top));
        }
    }

    /// <summary>
    /// Creates a rail and a lazy preview overlay.
    /// </summary>
    public ChatTurnNavigator()
    {
        Child = _previews;
        _previews.Clip = _previewClip;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.PropertyChanged += HandleTopLevelPropertyChanged;
        Reconnect();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attachmentVersion++;
        if (_topLevel is not null) _topLevel.PropertyChanged -= HandleTopLevelPropertyChanged;
        _topLevel = null;
        _isFramePending = false;
        DisposeHelper.DisposeToDefault(ref _index);
        ClearMotion();

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextElement.ForegroundProperty || change.Property == BackgroundProperty) InvalidateVisual();
        else if (change.Property == ChatContextProperty && _topLevel is not null) Reconnect();
        else if (change.Property == ReadingNodeProperty) UpdateReadingPosition();
        else if (change.Property == PreviewLineHeightProperty) RequestFrame();
        else if (change.Property == BoundsProperty || change.Property == LinePaddingProperty)
        {
            _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
            _offset.Value = Math.Clamp(_offset.Value, 0, MaximumOffset);
            UpdateReadingPosition();
            if (_isHovering && !HitTest(new Point(RailClip.Center.X, _pointerY))) LeavePointer();
            RequestFrame();
        }
        else if (change.Property == IsVisibleProperty || change.Property == IsEnabledProperty)
        {
            if (!IsVisible || !IsEnabled) ClearMotion();
            else RequestFrame();
        }
    }

    private void Reconnect()
    {
        DisposeHelper.DisposeToDefault(ref _index);
        ClearMotion();

        if (ChatContext is { } context)
        {
            _index = new ChatTurnNavigationIndex(context);
            _index.Changed += HandleIndexChanged;
        }

        _offset = new Spring { Value = MaximumOffset, Target = MaximumOffset };
        HandleIndexChanged();
    }

    private void HandleIndexChanged()
    {
        _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
        _offset.Value = Math.Clamp(_offset.Value, 0, MaximumOffset);
        UpdateReadingPosition();
        UpdateCards();
        RefreshPreviewText();
        InvalidateVisual();
        RequestFrame();
    }

    private void UpdateReadingPosition()
    {
        _readingIndex = -1;
        if (_index is not null)
        {
            for (var i = 0; i < _index.Turns.Count; i++)
            {
                if (!ReferenceEquals(_index.Turns[i].Node, ReadingNode)) continue;
                _readingIndex = i;
                break;
            }
        }

        // Reading follows the main viewport only outside pointer exploration. A new streamed
        // turn must not steal the rail offset from someone previewing older history.
        if (!_isHovering && _readingIndex >= 0)
        {
            var y = Origin + _readingIndex * Pitch - _offset.Target;
            var top = RailClip.Top + 8;
            var bottom = top + RailHeight - 16;
            if (y < top) _offset.Target -= top - y;
            else if (y > bottom) _offset.Target += y - bottom;
            _offset.Target = Math.Clamp(_offset.Target, 0, MaximumOffset);
        }

        InvalidateVisual();
        RequestFrame();
    }

    private void MovePointer(PointerEventArgs e)
    {
        _pointerY = e.GetPosition(this).Y;
        if (!_isHovering)
        {
            _focus = new Spring { Value = PointerPosition, Target = PointerPosition };
            _previewY = new Spring { Value = _pointerY, Target = _pointerY };
            _lastWakePosition = PointerPosition;
        }

        _isHovering = true;
        _presence.Target = 1;
        var position = PointerPosition;
        if (Math.Abs(position - _lastWakePosition) >= 2)
        {
            if (_wakes.Count == 4) _wakes.RemoveAt(0);
            _wakes.Add(new Wake(_lastWakePosition));
            _lastWakePosition = position;
        }

        _focus.Target = position;
        _previewY.Target = _pointerY;
        UpdateCards();
        RequestFrame();
    }

    private void LeavePointer()
    {
        _isHovering = false;
        _presence.Target = 0;
        RequestFrame();
    }

    private void Scroll(PointerWheelEventArgs e)
    {
        MovePointer(e);
        _offset.Target = Math.Clamp(_offset.Target - e.Delta.Y * Pitch * 3, 0, MaximumOffset);
        // Consume the wheel even at the ends: exploration must never scroll the conversation.
        e.Handled = true;
        RequestFrame();
    }

    private void Activate(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _index is null || TurnCount == 0) return;

        MovePointer(e);
        var node = _index.Turns[(int)Math.Round(PointerPosition)].Node;
        if (Target is { } target && ReferenceEquals(target.ChatContext, ChatContext)) target.RevealTurnAsync(node).Detach();
        e.Handled = true;
    }

    private void RequestFrame()
    {
        if (_isFramePending || _topLevel is null || !IsEffectivelyVisible || !IsEnabled) return;

        _isFramePending = true;
        var version = _attachmentVersion;
        _topLevel.RequestAnimationFrame(time =>
        {
            if (version != _attachmentVersion) return;
            _isFramePending = false;
            Animate(time);
        });
    }

    private void Animate(TimeSpan time)
    {
        if (_topLevel is null || !IsEffectivelyVisible || !IsEnabled)
        {
            ClearMotion();
            return;
        }

        var elapsed = _lastFrame is { } previous ? Math.Clamp((time - previous).TotalSeconds, 0, 0.05) : 1d / 60;
        _lastFrame = time;
        var isMoving = _offset.Step(elapsed, 22);
        if (_isHovering) _focus.Target = PointerPosition;
        isMoving |= _focus.Step(elapsed, 28);
        isMoving |= _presence.Step(elapsed, 22);
        isMoving |= _previewY.Step(elapsed, 28);
        for (var i = _wakes.Count - 1; i >= 0; i--)
        {
            _wakes[i].Age += elapsed;
            if (_wakes[i].Age > 0.45) _wakes.RemoveAt(i);
        }

        UpdateCards();
        InvalidateVisual();
        if (isMoving || _wakes.Count > 0) RequestFrame();
        else _lastFrame = null;
    }

    private void UpdateCards()
    {
        if (_index is null || TurnCount == 0 || _presence.Value < 0.002 && !_isHovering)
        {
            DisposeCards();
            _previews.Children.Clear();
            return;
        }

        var center = Math.Clamp(_focus.Value, 0, TurnCount - 1);
        // Keep previews inside the message viewport without expanding their input ownership.
        _previewClip.Rect = new Rect(Bounds.Size);
        var neighborCount = Math.Clamp((int)((Bounds.Height - 16) / (PreviewSlotHeight + 8) - 1) / 2, 0, 2);
        var first = Math.Max(0, (int)Math.Floor(center) - neighborCount);
        var last = Math.Min(TurnCount - 1, (int)Math.Ceiling(center) + neighborCount);

        // A seventh slot is unnecessary: floor/ceiling plus two neighbors bounds the tree at six.
        foreach (var pair in _cards.ToArray())
        {
            var shouldKeep = false;
            for (var i = first; i <= last; i++) shouldKeep |= ReferenceEquals(_index.Turns[i], pair.Key);
            if (shouldKeep) continue;
            pair.Value.PropertyChanged -= HandleCardPropertyChanged;
            pair.Value.Dispose();
            _previews.Children.Remove(pair.Value);
            _cards.Remove(pair.Key);
        }

        var width = Math.Max(0, Math.Min(320, Bounds.Width - RailWidth - 24));
        for (var i = first; i <= last; i++)
        {
            var entry = _index.Turns[i];
            if (!_cards.TryGetValue(entry, out var card))
            {
                card = new ChatTurnPreview
                {
                    RenderTransform = new MatrixTransform(),
                    RenderTransformOrigin = RelativePoint.TopLeft
                };
                _cards.Add(entry, card);
                _previews.Children.Add(card);
                card.PropertyChanged += HandleCardPropertyChanged;
                card.Observe(_index, entry);
            }
            card.Width = width;
        }

        var slotHeight = _cards.Values.Max(card => Math.Max(card.MinHeight, card.Bounds.Height));
        var spacing = slotHeight * 0.9 + 12;
        var halfHeight = Math.Min(slotHeight / 2, Bounds.Height / 2);
        var minimumY = halfHeight + Math.Min(center, neighborCount) * spacing + 8;
        var maximumY = Bounds.Height - halfHeight - Math.Min(TurnCount - 1 - center, neighborCount) * spacing - 8;
        // Preview motion is in viewport coordinates, independent of rail padding and scrolling.
        var anchorY = Math.Clamp(_previewY.Value, Math.Min(minimumY, maximumY), Math.Max(minimumY, maximumY));
        for (var i = first; i <= last; i++)
        {
            var card = _cards[_index.Turns[i]];
            var distance = i - center;
            var magnitude = Math.Abs(distance);
            var edgeScale = Math.Clamp(neighborCount + 1 - magnitude, 0, 1);
            var baseScale = Math.Max(0.72, 1 - 0.1 * magnitude);
            var scale = Math.Max(0.001, baseScale * edgeScale * _presence.Value);
            var height = Math.Max(card.MinHeight, card.Bounds.Height);
            // Collapse the upper edge card toward its bottom-left corner, and the lower one
            // toward its top-left corner. Account for the base depth scale separately, so the
            // inner edge does not retreat and open a gap while edgeScale approaches zero.
            var pivot = Math.Clamp(0.5 - distance * 0.5, 0, 1);
            var y = anchorY + distance * spacing - height * baseScale / 2 + height * (baseScale - scale) * pivot;
            card.ZIndex = 10 - (int)(magnitude * 2);
            if (card.RenderTransform is MatrixTransform transform)
                transform.Matrix = Matrix.CreateScale(scale, scale) *
                    Matrix.CreateTranslation(RailWidth + 8 - (1 - _presence.Value) * 24, y);
        }
    }

    private void RefreshPreviewText()
    {
        if (!IsEffectivelyVisible || !IsEnabled)
        {
            ClearMotion();
            return;
        }

        if (_index is null) return;

        foreach (var pair in _cards) pair.Value.Observe(_index, pair.Key);
    }

    private void ClearMotion()
    {
        _isHovering = false;
        _lastFrame = null;
        _presence = default;
        _focus = default;
        _previewY = default;
        _wakes.Clear();
        DisposeCards();
        _previews.Children.Clear();
    }

    private void HandleCardPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == MinHeightProperty || e.Property == BoundsProperty) RequestFrame();
    }

    private void HandleTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        if (e.NewValue is false) ClearMotion();
        else RequestFrame();
    }

    private void DisposeCards()
    {
        foreach (var card in _cards.Values)
        {
            card.PropertyChanged -= HandleCardPropertyChanged;
            card.Dispose();
        }
        _cards.Clear();
    }

    /// <inheritdoc />
    public bool HitTest(Point point) => TurnCount > 0 && RailHeight > 0 && RailClip.Width > 0 && HitBounds.Contains(point);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Drawing the backdrop does not expand input ownership: ICustomHitTest still restricts
        // pointer input to the actual marks, allowing the remaining chat area to stay interactive.
        if (Background is { } background && _presence.Value > 0)
        {
            using (context.PushOpacity(_presence.Value)) context.FillRectangle(background, new Rect(Bounds.Size));
        }

        var clip = RailClip;
        if (clip.Height <= 0) return;
        var top = clip.Top;
        var origin = Origin - _offset.Value;
        var first = Math.Max(0, (int)Math.Floor((top - origin) / Pitch) - 2);
        var last = Math.Min(TurnCount - 1, (int)Math.Ceiling((clip.Bottom - origin) / Pitch) + 2);
        var brush = TextElement.GetForeground(this) ?? Brushes.Gray;
        var normalPen = new Pen(brush, 1.5, lineCap: PenLineCap.Round);
        var readingPen = new Pen(brush, 2, lineCap: PenLineCap.Round);
        var fadeLength = Math.Min(Pitch * 2, clip.Height / 2);
        // Fade only toward hidden content. Blend the fade strength out as the actual animated
        // offset reaches an end, instead of switching opacity abruptly at the scroll boundary.
        var topFade = Math.Clamp(_offset.Value / fadeLength, 0, 1);
        var bottomFade = Math.Clamp((MaximumOffset - _offset.Value) / fadeLength, 0, 1);
        using (context.PushClip(clip))
        {
            for (var i = first; i <= last; i++)
            {
                var influence = Math.Exp(-Math.Pow((i - _focus.Value) / 2.4, 2)) * _presence.Value;
                foreach (var wake in _wakes)
                {
                    var strength = 0.65 * Math.Pow(1 - wake.Age / 0.45, 2);
                    influence = Math.Max(influence, strength * Math.Exp(-Math.Pow((i - wake.Position) / 1.8, 2)));
                }

                var isReading = i == _readingIndex;
                var length = 7 + influence * 23;
                var y = origin + i * Pitch;
                var edge = (1 - topFade * (1 - Math.Clamp((y - top) / fadeLength, 0, 1))) *
                    (1 - bottomFade * (1 - Math.Clamp((clip.Bottom - y) / fadeLength, 0, 1)));
                using (context.PushOpacity((0.24 + 0.7 * Math.Max(influence, isReading ? 0.7 : 0)) * edge))
                    context.DrawLine(isReading ? readingPen : normalPen, new Point(clip.Left, y), new Point(clip.Left + length, y));
            }
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        MovePointer(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        MovePointer(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        LeavePointer();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Scroll(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Activate(e);
    }

    private sealed class Wake(double position)
    {
        public double Position { get; } = position;
        public double Age { get; set; }
    }

    private struct Spring
    {
        public double Value { get; set; }
        public double Target { get; set; }

        private double _velocity;

        public bool Step(double elapsed, double frequency)
        {
            // Analytic critically damped spring: retarget without discarding velocity and remain
            // stable at different refresh rates. No layout properties participate in the motion.
            var displacement = Value - Target;
            var coefficient = _velocity + frequency * displacement;
            var decay = Math.Exp(-frequency * elapsed);
            Value = Target + (displacement + coefficient * elapsed) * decay;
            _velocity = (_velocity - frequency * coefficient * elapsed) * decay;

            if (Math.Abs(Value - Target) > 0.001 || Math.Abs(_velocity) > 0.001) return true;
            Value = Target;
            _velocity = 0;
            return false;
        }
    }
}