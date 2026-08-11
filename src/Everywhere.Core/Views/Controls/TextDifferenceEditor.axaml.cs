using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Plugins;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Displays a lazily projected unified diff and lets the user accept, reject, and comment on each change.
/// </summary>
public partial class TextDifferenceEditor : TemplatedControl
{
    public static FuncTemplate<Panel?> RegionItemsPanelTemplate { get; } =
        new(static () => new RegionOverlayVirtualizingPanel());

    public static readonly StyledProperty<int> AddedLineCountProperty =
        TextDifferenceSummaryView.AddedLineCountProperty.AddOwner<TextDifferenceSummaryView>();

    public static readonly StyledProperty<int> RemovedLineCountProperty =
        TextDifferenceSummaryView.RemovedLineCountProperty.AddOwner<TextDifferenceSummaryView>();

    public static readonly StyledProperty<TextDifference?> TextDifferenceProperty =
        AvaloniaProperty.Register<TextDifferenceEditor, TextDifference?>(nameof(TextDifference));

    public static readonly StyledProperty<string?> OriginalTextProperty =
        AvaloniaProperty.Register<TextDifferenceEditor, string?>(nameof(OriginalText));

    public static readonly StyledProperty<bool> OnlyAcceptedProperty =
        AvaloniaProperty.Register<TextDifferenceEditor, bool>(nameof(OnlyAccepted));

    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<TextDifferenceEditor, bool>(nameof(ShowLineNumbers));

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<TextDifferenceEditor, double>(
            nameof(LineHeight),
            21,
            validate: static value => value > 0 && double.IsFinite(value));

    public static readonly DirectProperty<TextDifferenceEditor, AvaloniaList<TextDifferenceLine>> RowsProperty =
        AvaloniaProperty.RegisterDirect<TextDifferenceEditor, AvaloniaList<TextDifferenceLine>>(nameof(Rows), static editor => editor.Rows);

    public static readonly DirectProperty<TextDifferenceEditor, AvaloniaList<TextDifferenceRegionItem>> RegionsProperty =
        AvaloniaProperty.RegisterDirect<TextDifferenceEditor, AvaloniaList<TextDifferenceRegionItem>>(
            nameof(Regions),
            static editor => editor.Regions);

    public static readonly DirectProperty<TextDifferenceEditor, double> HorizontalOffsetProperty =
        AvaloniaProperty.RegisterDirect<TextDifferenceEditor, double>(
            nameof(HorizontalOffset),
            static editor => editor.HorizontalOffset);

    public static readonly DirectProperty<TextDifferenceEditor, double> ScrollViewportWidthProperty =
        AvaloniaProperty.RegisterDirect<TextDifferenceEditor, double>(
            nameof(ScrollViewportWidth),
            static editor => editor.ScrollViewportWidth);

    public static readonly DirectProperty<TextDifferenceEditor, bool?> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<TextDifferenceEditor, bool?>(nameof(IsLoading), static editor => editor.IsLoading);

    public int AddedLineCount
    {
        get => GetValue(AddedLineCountProperty);
        set => SetValue(AddedLineCountProperty, value);
    }

    public int RemovedLineCount
    {
        get => GetValue(RemovedLineCountProperty);
        set => SetValue(RemovedLineCountProperty, value);
    }

    public TextDifference? TextDifference
    {
        get => GetValue(TextDifferenceProperty);
        set => SetValue(TextDifferenceProperty, value);
    }

    public string? OriginalText
    {
        get => GetValue(OriginalTextProperty);
        set => SetValue(OriginalTextProperty, value);
    }

    public bool OnlyAccepted
    {
        get => GetValue(OnlyAcceptedProperty);
        set => SetValue(OnlyAcceptedProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    /// <summary>
    /// Gets or sets the exact height of every displayed difference row.
    /// </summary>
    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    /// <summary>
    /// Gets the flattened rows consumed by the virtualizing items control.
    /// </summary>
    public AvaloniaList<TextDifferenceLine> Rows { get; } = [];

    /// <summary>
    /// Gets the reviewable regions projected over the fixed-height display rows.
    /// </summary>
    public AvaloniaList<TextDifferenceRegionItem> Regions { get; } = [];

    public double HorizontalOffset
    {
        get;
        private set => SetAndRaise(HorizontalOffsetProperty, ref field, value);
    }

    public double ScrollViewportWidth
    {
        get;
        private set => SetAndRaise(ScrollViewportWidthProperty, ref field, value);
    }

    /// <summary>
    /// null for error, true for loading, false for loaded.
    /// </summary>
    public bool? IsLoading
    {
        get;
        private set => SetAndRaise(IsLoadingProperty, ref field, value);
    } = true;

    public ScrollViewer? ScrollViewer
    {
        get;
        set
        {
            DisconnectScrollViewer();
            ConnectScrollViewer(field = value);
        }
    }

    /// <summary>
    /// Raised when the user confirms the current per-change review selection.
    /// </summary>
    public event EventHandler? ReviewConfirmed;

    private CancellationTokenSource? _loadCancellationTokenSource;
    private IDisposable? _scrollOffsetSubscription;
    private IDisposable? _scrollViewportSubscription;
    private TextDifferenceProjection? _projection;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (VisualRoot is null) return;
        if (change.Property != TextDifferenceProperty &&
            change.Property != OriginalTextProperty &&
            change.Property != OnlyAcceptedProperty)
        {
            return;
        }

        QueueLoad();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ScrollViewer is null)
        {
            ConnectScrollViewer(
                this.GetVisualDescendants()
                    .AsValueEnumerable()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault(static scrollViewer => scrollViewer.Name == "PART_ScrollViewer"));
        }

        QueueLoad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelLoad();
        DisconnectScrollViewer();
        base.OnDetachedFromVisualTree(e);
    }

    private void ConnectScrollViewer(ScrollViewer? scrollViewer)
    {
        if (scrollViewer is null) return;

        _scrollOffsetSubscription = scrollViewer
            .GetObservable(ScrollViewer.OffsetProperty)
            .Subscribe(offset => HorizontalOffset = offset.X);
        _scrollViewportSubscription = scrollViewer
            .GetObservable(ScrollViewer.ViewportProperty)
            .Subscribe(viewport => ScrollViewportWidth = viewport.Width);
        HorizontalOffset = scrollViewer.Offset.X;
        ScrollViewportWidth = scrollViewer.Viewport.Width;
    }

    private void DisconnectScrollViewer()
    {
        _scrollOffsetSubscription?.Dispose();
        _scrollOffsetSubscription = null;
        _scrollViewportSubscription?.Dispose();
        _scrollViewportSubscription = null;
        HorizontalOffset = 0;
        ScrollViewportWidth = 0;
    }

    [RelayCommand]
    private void AcceptBlock(TextChange? change)
    {
        if (change is null) return;
        change.IsAccepted = true;
        if (OnlyAccepted) QueueLoad();
    }

    [RelayCommand]
    private void RejectBlock(TextChange? change)
    {
        if (change is null) return;
        change.IsAccepted = false;
        if (OnlyAccepted) QueueLoad();
    }

    [RelayCommand]
    private void AcceptAll()
    {
        if (TextDifference is null) return;
        TextDifference.AcceptAll();
        if (OnlyAccepted) QueueLoad();
    }

    [RelayCommand]
    private void RejectAll()
    {
        if (TextDifference is null) return;
        TextDifference.RejectAll();
        if (OnlyAccepted) QueueLoad();
    }

    [RelayCommand]
    private void Confirm() => ReviewConfirmed?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task ExpandContextAsync(TextDifferenceOmittedLine? line)
    {
        if (line is null || _projection is not { } projection) return;
        if (line.IsLoading) return;

        line.IsLoading = true;
        try
        {
            var cancellationToken = _loadCancellationTokenSource?.Token ?? CancellationToken.None;
            var expanded = await Task.Run(
                () => TextDifferenceProjectionBuilder.ExpandContext(projection, line, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var index = Rows.IndexOf(line);
            if (index < 0) return;

            Rows.RemoveAt(index);
            for (var offset = 0; offset < expanded.Count; offset++)
            {
                Rows.Insert(index + offset, expanded[offset]);
            }

            Regions.Clear();
            Regions.AddRange(TextDifferenceProjectionBuilder.BuildRegions(Rows));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.ForContext<TextDifferenceEditor>().Warning(ex, "Failed to expand an omitted diff context range.");
        }
        finally
        {
            line.IsLoading = false;
        }
    }

    private void QueueLoad()
    {
        CancelLoad();
        Rows.Clear();
        Regions.Clear();
        _projection = null;

        if (TextDifference is not { } difference || OriginalText is not { } originalText)
        {
            IsLoading = false;
            return;
        }

        var changes = difference.GetFilteredChanges(OnlyAccepted).AsValueEnumerable().ToArray();
        var cancellationTokenSource = new CancellationTokenSource();
        _loadCancellationTokenSource = cancellationTokenSource;
        LoadAsync(originalText, changes, cancellationTokenSource).Detach();
    }

    private void CancelLoad()
    {
        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource?.Dispose();
        _loadCancellationTokenSource = null;
        IsLoading = false;
    }

    private async Task LoadAsync(string originalText, IReadOnlyList<TextChange> changes, CancellationTokenSource cancellationTokenSource)
    {
        IsLoading = true;

        var cancellationToken = cancellationTokenSource.Token;
        try
        {
            var projection = await Task.Run(
                () => TextDifferenceProjectionBuilder.Build(originalText, changes, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource)) return;

            _projection = projection;
            Rows.AddRange(projection.Rows);
            Regions.AddRange(projection.Regions);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource)) return;
            IsLoading = null;
            Log.ForContext<TextDifferenceEditor>().Warning(ex, "Failed to build the text difference presentation.");
        }
        finally
        {
            if (IsLoading is true && ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Virtualizes review regions whose positions and heights are exact multiples of the editor's row height.
    /// </summary>
    /// <remarks>
    /// Unlike a stack panel, visual ranges may overlap. The panel therefore derives its extent from
    /// the row projection and realizes regions by interval intersection instead of accumulated item sizes.
    /// </remarks>
    private sealed class RegionOverlayVirtualizingPanel : VirtualizingPanel
    {
        private const double CacheViewportLength = 1;
        private const double ActionHeight = 36;
        private const double ActionTopInset = 4;

        private static readonly object ItemIsItsOwnContainer = new();

        private readonly Dictionary<int, ContainerSlot> _realized = [];
        private readonly Dictionary<Control, int> _containerIndexes = [];
        private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
        private readonly List<int> _recycleIndexes = [];
        private TextDifferenceEditor? _editor;
        private ScrollViewer? _scrollViewer;
        private IDisposable? _offsetSubscription;
        private IDisposable? _viewportSubscription;
        private IDisposable? _lineHeightSubscription;
        private double _viewportTop;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _editor = this.FindAncestorOfType<TextDifferenceEditor>();
            _scrollViewer = this.FindAncestorOfType<ScrollViewer>();
            _lineHeightSubscription = _editor?.GetObservable(LineHeightProperty).Subscribe(_ => InvalidateMeasure());
            _offsetSubscription = _scrollViewer?.GetObservable(ScrollViewer.OffsetProperty).Subscribe(_ => InvalidateMeasure());
            _viewportSubscription = _scrollViewer?.GetObservable(ScrollViewer.ViewportProperty).Subscribe(_ => InvalidateMeasure());
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _offsetSubscription?.Dispose();
            _offsetSubscription = null;
            _viewportSubscription?.Dispose();
            _viewportSubscription = null;
            _lineHeightSubscription?.Dispose();
            _lineHeightSubscription = null;
            _scrollViewer = null;
            _editor = null;
            RecycleAll();
            base.OnDetachedFromVisualTree(e);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_editor is not { } editor || _scrollViewer is not { } scrollViewer || Items.Count == 0)
            {
                RecycleAll();
                return default;
            }

            var lineHeight = editor.LineHeight;
            var totalHeight = editor.Rows.Count * lineHeight;
            var viewportHeight = scrollViewer.Viewport.Height;
            _viewportTop = Math.Max(0, scrollViewer.Offset.Y - GetPanelTopWithinScrollContent(scrollViewer));
            var cache = Math.Max(0, viewportHeight * CacheViewportLength);
            var start = Math.Max(0, _viewportTop - cache);
            var end = Math.Min(totalHeight, _viewportTop + viewportHeight + cache);
            var range = FindRealizationRange(start, end, lineHeight);
            RealizeRange(range.StartIndex, range.EndIndex, GetDesiredWidth(availableSize, scrollViewer), lineHeight);

            return new Size(GetDesiredWidth(availableSize, scrollViewer), totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_editor is not { } editor) return finalSize;

            var lineHeight = editor.LineHeight;
            foreach (var pair in _realized)
            {
                if (GetRegion(pair.Key) is not { } region) continue;

                region.UpdateLayoutMetrics(lineHeight, _viewportTop, ActionHeight, ActionTopInset);
                pair.Value.Container.Arrange(
                    new Rect(
                        0,
                        region.VisualStartRow * lineHeight,
                        finalSize.Width,
                        region.VisualRowSpan * lineHeight));
            }

            return finalSize;
        }

        protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(items, e);
            RecycleAll();
            InvalidateMeasure();
        }

        protected override IEnumerable<Control> GetRealizedContainers() =>
            _realized.OrderBy(static pair => pair.Key).Select(static pair => pair.Value.Container);

        protected override Control? ContainerFromIndex(int index) =>
            _realized.GetValueOrDefault(index)?.Container;

        protected override int IndexFromContainer(Control container) =>
            _containerIndexes.GetValueOrDefault(container, -1);

        protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
        {
            var count = Items.Count;
            if (count == 0) return null;

            var index = from is Control control ? IndexFromContainer(control) : -1;
            var target = direction switch
            {
                NavigationDirection.First => 0,
                NavigationDirection.Last => count - 1,
                NavigationDirection.Next or NavigationDirection.Down => index + 1,
                NavigationDirection.Previous or NavigationDirection.Up => index - 1,
                _ => index
            };

            if (wrap)
            {
                if (target < 0) target = count - 1;
                if (target >= count) target = 0;
            }

            return target >= 0 && target < count ? ScrollIntoView(target) : from;
        }

        protected override Control? ScrollIntoView(int index)
        {
            if (index < 0 || index >= Items.Count ||
                _editor is not { } editor ||
                _scrollViewer is not { } scrollViewer ||
                GetRegion(index) is not { } region)
            {
                return null;
            }

            if (ContainerFromIndex(index) is { } realized)
            {
                realized.BringIntoView();
                return realized;
            }

            var panelTop = GetPanelTopWithinScrollContent(scrollViewer);
            var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var y = Math.Clamp(panelTop + region.VisualStartRow * editor.LineHeight, 0, maximum);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, y);
            InvalidateMeasure();
            return null;
        }

        private (int StartIndex, int EndIndex) FindRealizationRange(double start, double end, double lineHeight)
        {
            var count = Items.Count;
            if (count == 0 || end <= start) return (-1, -1);

            var low = 0;
            var high = count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (GetRegion(middle) is { } region && region.VisualEndRow * lineHeight <= start) low = middle + 1;
                else high = middle;
            }

            var first = low;
            var last = first - 1;
            while (last + 1 < count && GetRegion(last + 1) is { } region && region.VisualStartRow * lineHeight < end)
            {
                last++;
            }

            return first <= last ? (first, last) : (-1, -1);
        }

        private void RealizeRange(int startIndex, int endIndex, double width, double lineHeight)
        {
            _recycleIndexes.Clear();
            foreach (var index in _realized.Keys)
            {
                if (index < startIndex || index > endIndex) _recycleIndexes.Add(index);
            }
            foreach (var index in _recycleIndexes) Recycle(index);

            if (startIndex < 0 || endIndex < startIndex) return;

            for (var index = startIndex; index <= endIndex; index++)
            {
                if (GetRegion(index) is not { } region) continue;

                var container = _realized.GetValueOrDefault(index)?.Container ?? CreateOrRecycle(index);
                if (container is null) continue;

                region.UpdateLayoutMetrics(lineHeight, _viewportTop, ActionHeight, ActionTopInset);
                container.Measure(new Size(width, region.VisualRowSpan * lineHeight));
            }
        }

        private Control? CreateOrRecycle(int index)
        {
            var generator = ItemContainerGenerator;
            if (generator is null) return null;

            var item = Items[index];
            var needsContainer = generator.NeedsContainer(item, index, out var recycleKey);
            var container = needsContainer ? TryTakeRecycled(recycleKey) ?? generator.CreateContainer(item, index, recycleKey) : item as Control;
            if (container is null) return null;
            if (!needsContainer) recycleKey = ItemIsItsOwnContainer;

            generator.PrepareItemContainer(container, item, index);

            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
            _realized[index] = new ContainerSlot(container, recycleKey);
            _containerIndexes[container] = index;
            return container;
        }

        private Control? TryTakeRecycled(object? recycleKey)
        {
            if (recycleKey is null || !_recyclePool.TryGetValue(recycleKey, out var pool) || pool.Count == 0)
            {
                return null;
            }

            return pool.Pop();
        }

        private void Recycle(int index)
        {
            if (!_realized.Remove(index, out var slot)) return;

            _containerIndexes.Remove(slot.Container);
            var generator = ItemContainerGenerator;
            if (!ReferenceEquals(slot.RecycleKey, ItemIsItsOwnContainer)) generator?.ClearItemContainer(slot.Container);
            RemoveInternalChild(slot.Container);

            if (slot.RecycleKey is null || ReferenceEquals(slot.RecycleKey, ItemIsItsOwnContainer)) return;
            if (!_recyclePool.TryGetValue(slot.RecycleKey, out var pool))
            {
                pool = new Stack<Control>();
                _recyclePool.Add(slot.RecycleKey, pool);
            }

            pool.Push(slot.Container);
        }

        private void RecycleAll()
        {
            _recycleIndexes.Clear();
            _recycleIndexes.AddRange(_realized.Keys);
            foreach (var index in _recycleIndexes) Recycle(index);
            _containerIndexes.Clear();
        }

        private TextDifferenceRegionItem? GetRegion(int index) =>
            index >= 0 && index < Items.Count ? Items[index] as TextDifferenceRegionItem : null;

        private double GetPanelTopWithinScrollContent(ScrollViewer scrollViewer)
        {
            if (scrollViewer.Content is Visual content &&
                this.TranslatePoint(default, content) is { } position)
            {
                return Math.Max(0, position.Y);
            }

            if (this.TranslatePoint(default, scrollViewer) is { } scrollPosition)
                return Math.Max(0, scrollPosition.Y + scrollViewer.Offset.Y);

            return 0;
        }

        private double GetDesiredWidth(Size availableSize, ScrollViewer scrollViewer)
        {
            if (scrollViewer.Viewport.Width > 0 && double.IsFinite(scrollViewer.Viewport.Width)) return scrollViewer.Viewport.Width;
            if (double.IsFinite(availableSize.Width)) return availableSize.Width;
            return _editor?.Bounds.Width ?? 0;
        }

        private sealed record ContainerSlot(Control Container, object? RecycleKey);
    }
}

/// <summary>
/// Holds the source lines needed to expand initially omitted context without recomputing the diff.
/// </summary>
internal sealed class TextDifferenceProjection(
    IReadOnlyList<TextDifferenceLine> rows,
    IReadOnlyList<TextDifferenceRegionItem> regions,
    IReadOnlyList<TextDifferenceSourceLine> originalLines
)
{
    public IReadOnlyList<TextDifferenceLine> Rows { get; } = rows;

    public IReadOnlyList<TextDifferenceRegionItem> Regions { get; } = regions;

    public IReadOnlyList<TextDifferenceSourceLine> OriginalLines { get; } = originalLines;
}

/// <summary>
/// Maps one source line to both its character range and display content.
/// </summary>
internal readonly record struct TextDifferenceSourceLine(int Start, int End, string Content);

/// <summary>
/// Projects original-relative changes into a unified, context-folded sequence of display rows.
/// </summary>
internal static class TextDifferenceProjectionBuilder
{
    private const int DefaultContextLineCount = 3;
    private const int RegionContextLineCount = 1;

    /// <summary>
    /// Builds a virtualizable unified-diff projection while folding distant unchanged ranges.
    /// </summary>
    public static TextDifferenceProjection Build(
        string original,
        IReadOnlyList<TextChange> changes,
        CancellationToken cancellationToken,
        int contextLineCount = DefaultContextLineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextLineCount);
        var sourceLines = SplitLines(original);
        var rows = new List<TextDifferenceLine>();
        if (changes.Count == 0) return new TextDifferenceProjection(rows, [], sourceLines);

        var originalLineIndex = 0;
        var oldLineNumber = 1;
        var newLineNumber = 1;
        for (var changeIndex = 0; changeIndex < changes.Count; changeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = changes[changeIndex];
            var startLineIndex = FindStartLine(sourceLines, originalLineIndex, change.Range.Start);
            var endLineIndex = FindEndLine(sourceLines, startLineIndex, change.Range.End);

            AddContextGap(
                rows,
                sourceLines,
                originalLineIndex,
                startLineIndex,
                hasChangeBefore: changeIndex > 0,
                hasChangeAfter: true,
                contextLineCount,
                ref oldLineNumber,
                ref newLineNumber,
                cancellationToken);

            for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(
                    new TextDifferenceRemovedLine(
                        oldLineNumber++,
                        sourceLines[lineIndex].Content,
                        change));
            }

            foreach (var text in SplitReplacementLines(change.NewText))
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new TextDifferenceAddedLine(newLineNumber++, text, change));
            }

            originalLineIndex = endLineIndex;
        }

        AddContextGap(
            rows,
            sourceLines,
            originalLineIndex,
            sourceLines.Count,
            hasChangeBefore: true,
            hasChangeAfter: false,
            contextLineCount,
            ref oldLineNumber,
            ref newLineNumber,
            cancellationToken);
        return new TextDifferenceProjection(rows, BuildRegions(rows), sourceLines);
    }

    /// <summary>
    /// Builds overlapping visual regions and non-overlapping pointer interaction ranges over a row projection.
    /// </summary>
    public static IReadOnlyList<TextDifferenceRegionItem> BuildRegions(
        IReadOnlyList<TextDifferenceLine> rows,
        int contextLineCount = RegionContextLineCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contextLineCount);
        var cores = new List<RegionCore>();
        var index = 0;
        while (index < rows.Count)
        {
            if (rows[index] is not TextDifferenceChangedLine changed)
            {
                index++;
                continue;
            }

            var start = index++;
            while (index < rows.Count && rows[index] is TextDifferenceChangedLine next && ReferenceEquals(next.Change, changed.Change))
            {
                index++;
            }

            cores.Add(new RegionCore(changed.Change, start, index));
        }

        if (cores.Count == 0) return [];

        var visualStarts = new int[cores.Count];
        var visualEnds = new int[cores.Count];
        var interactionStarts = new int[cores.Count];
        var interactionEnds = new int[cores.Count];
        for (var i = 0; i < cores.Count; i++)
        {
            visualStarts[i] = Math.Max(0, cores[i].StartRow - contextLineCount);
            visualEnds[i] = Math.Min(rows.Count, cores[i].EndRow + contextLineCount);
            interactionStarts[i] = visualStarts[i];
            interactionEnds[i] = visualEnds[i];
        }

        for (var i = 1; i < cores.Count; i++)
        {
            if (visualEnds[i - 1] <= visualStarts[i]) continue;

            var boundary = cores[i - 1].EndRow + (cores[i].StartRow - cores[i - 1].EndRow + 1) / 2;
            interactionEnds[i - 1] = Math.Min(interactionEnds[i - 1], boundary);
            interactionStarts[i] = Math.Max(interactionStarts[i], boundary);
        }

        var regions = new List<TextDifferenceRegionItem>(cores.Count);
        for (var i = 0; i < cores.Count; i++)
        {
            regions.Add(
                new TextDifferenceRegionItem(
                    cores[i].Change,
                    visualStarts[i],
                    visualEnds[i],
                    interactionStarts[i],
                    interactionEnds[i]));
        }

        return regions;
    }

    /// <summary>
    /// Materializes the source rows represented by one folded context row.
    /// </summary>
    public static IReadOnlyList<TextDifferenceLine> ExpandContext(
        TextDifferenceProjection projection,
        TextDifferenceOmittedLine omitted,
        CancellationToken cancellationToken)
    {
        var rows = new List<TextDifferenceLine>(omitted.LineCount);
        var oldLineNumber = omitted.OldLineNumber;
        var newLineNumber = omitted.NewLineNumber;
        AddContextLines(
            rows,
            projection.OriginalLines,
            omitted.StartLineIndex,
            omitted.EndLineIndex,
            ref oldLineNumber,
            ref newLineNumber,
            cancellationToken);
        return rows;
    }

    private static void AddContextGap(
        List<TextDifferenceLine> rows,
        IReadOnlyList<TextDifferenceSourceLine> sourceLines,
        int start,
        int end,
        bool hasChangeBefore,
        bool hasChangeAfter,
        int contextLineCount,
        ref int oldLineNumber,
        ref int newLineNumber,
        CancellationToken cancellationToken)
    {
        var count = end - start;
        if (count <= 0) return;

        var leadingCount = hasChangeBefore ? Math.Min(contextLineCount, count) : 0;
        var trailingCount = hasChangeAfter ? Math.Min(contextLineCount, count - leadingCount) : 0;
        if (leadingCount + trailingCount >= count)
        {
            AddContextLines(
                rows,
                sourceLines,
                start,
                end,
                ref oldLineNumber,
                ref newLineNumber,
                cancellationToken);
            return;
        }

        var omittedStart = start + leadingCount;
        var omittedEnd = end - trailingCount;
        AddContextLines(
            rows,
            sourceLines,
            start,
            omittedStart,
            ref oldLineNumber,
            ref newLineNumber,
            cancellationToken);
        AddOmittedRow(rows, omittedStart, omittedEnd, ref oldLineNumber, ref newLineNumber);
        AddContextLines(
            rows,
            sourceLines,
            omittedEnd,
            end,
            ref oldLineNumber,
            ref newLineNumber,
            cancellationToken);
    }

    private static void AddContextLines(
        List<TextDifferenceLine> rows,
        IReadOnlyList<TextDifferenceSourceLine> sourceLines,
        int start,
        int end,
        ref int oldLineNumber,
        ref int newLineNumber,
        CancellationToken cancellationToken)
    {
        for (var lineIndex = start; lineIndex < end; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(
                new TextDifferenceContextLine(
                    oldLineNumber++,
                    newLineNumber++,
                    sourceLines[lineIndex].Content));
        }
    }

    private static void AddOmittedRow(
        List<TextDifferenceLine> rows,
        int start,
        int end,
        ref int oldLineNumber,
        ref int newLineNumber)
    {
        var count = end - start;
        if (count <= 0) return;

        rows.Add(new TextDifferenceOmittedLine(count, start, end, oldLineNumber, newLineNumber));
        oldLineNumber += count;
        newLineNumber += count;
    }

    private static int FindStartLine(
        IReadOnlyList<TextDifferenceSourceLine> sourceLines,
        int minimumIndex,
        int characterOffset)
    {
        var index = minimumIndex;
        while (index < sourceLines.Count && sourceLines[index].End <= characterOffset)
        {
            index++;
        }

        return index;
    }

    private static int FindEndLine(
        IReadOnlyList<TextDifferenceSourceLine> sourceLines,
        int startIndex,
        int characterOffset)
    {
        var index = startIndex;
        while (index < sourceLines.Count && sourceLines[index].Start < characterOffset)
        {
            index++;
        }

        return index;
    }

    private static IReadOnlyList<TextDifferenceSourceLine> SplitLines(string text)
    {
        var lines = new List<TextDifferenceSourceLine>();
        var index = 0;
        var start = 0;
        while (index < text.Length)
        {
            if (text[index] != '\r' && text[index] != '\n')
            {
                index++;
                continue;
            }

            var lineEndingLength = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            lines.Add(new TextDifferenceSourceLine(start, index + lineEndingLength, text[start..index]));
            index += lineEndingLength;
            start = index;
        }

        if (start < text.Length)
        {
            lines.Add(new TextDifferenceSourceLine(start, text.Length, text[start..]));
        }

        return lines;
    }

    private static IReadOnlyList<string> SplitReplacementLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var lines = new List<string>();
        var index = 0;
        var start = 0;
        while (index < text.Length)
        {
            if (text[index] != '\r' && text[index] != '\n')
            {
                index++;
                continue;
            }

            lines.Add(text[start..index]);
            index += text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            start = index;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private readonly record struct RegionCore(TextChange Change, int StartRow, int EndRow);
}