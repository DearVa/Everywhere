using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using Everywhere.Collections;

namespace Everywhere.Interactions;

/// <summary>
/// Incrementally fills a <see cref="ScrollViewer"/> while it is near the end of its content.
/// </summary>
/// <remarks>
/// One loader session spans all page requests needed to fill the current viewport. This keeps the
/// loader busy across asynchronous layout passes instead of briefly toggling between pages.
/// </remarks>
public sealed class IncrementalLoadBehavior : Behavior<ScrollViewer>
{
    /// <summary>
    /// Defines the <see cref="Loader"/> property.
    /// </summary>
    public static readonly StyledProperty<IIncrementalLoader?> LoaderProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, IIncrementalLoader?>(nameof(Loader));

    /// <summary>
    /// Defines the <see cref="PageSize"/> property.
    /// </summary>
    public static readonly StyledProperty<int> PageSizeProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, int>(nameof(PageSize), 20);

    /// <summary>
    /// Defines the <see cref="Threshold"/> property.
    /// </summary>
    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, double>(nameof(Threshold), 8d);

    /// <summary>
    /// Defines the <see cref="EstimatedItemHeight"/> property.
    /// </summary>
    public static readonly StyledProperty<double> EstimatedItemHeightProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, double>(nameof(EstimatedItemHeight), double.NaN);

    /// <summary>
    /// Defines the <see cref="OverscanItemCount"/> property.
    /// </summary>
    public static readonly StyledProperty<int> OverscanItemCountProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, int>(nameof(OverscanItemCount), 2);

    /// <summary>
    /// Defines the <see cref="MaximumLoadCount"/> property.
    /// </summary>
    public static readonly StyledProperty<int> MaximumLoadCountProperty =
        AvaloniaProperty.Register<IncrementalLoadBehavior, int>(nameof(MaximumLoadCount), 100);

    /// <summary>
    /// Gets or sets the data source to load.
    /// </summary>
    public IIncrementalLoader? Loader
    {
        get => GetValue(LoaderProperty);
        set => SetValue(LoaderProperty, value);
    }

    /// <summary>
    /// Gets or sets the fallback number of result items requested per page.
    /// </summary>
    public int PageSize
    {
        get => GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets the distance from the end at which loading begins.
    /// </summary>
    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    /// <summary>
    /// Gets or sets an optional item-height estimate used only to size an underfilled viewport's
    /// first request. Non-positive, NaN, and infinite values disable estimation.
    /// </summary>
    public double EstimatedItemHeight
    {
        get => GetValue(EstimatedItemHeightProperty);
        set => SetValue(EstimatedItemHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the number of estimated items requested beyond the visible shortfall.
    /// </summary>
    public int OverscanItemCount
    {
        get => GetValue(OverscanItemCountProperty);
        set => SetValue(OverscanItemCountProperty, value);
    }

    /// <summary>
    /// Gets or sets the upper bound for a single load request.
    /// </summary>
    public int MaximumLoadCount
    {
        get => GetValue(MaximumLoadCountProperty);
        set => SetValue(MaximumLoadCountProperty, value);
    }

    private CancellationTokenSource? _lifetimeCancellation;
    private IIncrementalLoader? _subscribedLoader;
    private bool _evaluationQueued;
    private bool _isFilling;
    private bool _reevaluateAfterFill;

    protected override void OnAttached()
    {
        base.OnAttached();

        AssociatedObject!.ScrollChanged += HandleScrollChanged;
        SubscribeLoader(Loader);
        ResetLifetime();
        QueueEvaluation();
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } scrollViewer)
        {
            scrollViewer.ScrollChanged -= HandleScrollChanged;
        }

        SubscribeLoader(null);
        CancelLifetime();
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LoaderProperty)
        {
            SubscribeLoader(change.NewValue as IIncrementalLoader);
            ResetLifetime();
            QueueEvaluation();
        }
        else if (change.Property == IsEnabledProperty)
        {
            ResetLifetime();
            QueueEvaluation();
        }
        else if (change.Property == PageSizeProperty ||
                 change.Property == ThresholdProperty ||
                 change.Property == EstimatedItemHeightProperty ||
                 change.Property == OverscanItemCountProperty ||
                 change.Property == MaximumLoadCountProperty)
        {
            QueueEvaluation();
        }
    }

    private void SubscribeLoader(IIncrementalLoader? loader)
    {
        if (ReferenceEquals(loader, _subscribedLoader)) return;

        if (_subscribedLoader is { } previous)
        {
            previous.PropertyChanged -= HandleLoaderPropertyChanged;
        }

        _subscribedLoader = loader;
        if (loader is not null)
        {
            loader.PropertyChanged += HandleLoaderPropertyChanged;
        }
    }

    private void HandleLoaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IIncrementalLoader.HasMoreItems))
        {
            Dispatcher.UIThread.PostOnDemand(QueueEvaluation);
        }
    }

    private void HandleScrollChanged(object? sender, ScrollChangedEventArgs e) => QueueEvaluation();

    private void QueueEvaluation()
    {
        if (AssociatedObject is null || !IsEnabled || _evaluationQueued) return;

        if (_isFilling)
        {
            _reevaluateAfterFill = true;
            return;
        }

        _evaluationQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _evaluationQueued = false;
                if (AssociatedObject is null || !IsEnabled) return;

                FillViewportAsync(_lifetimeCancellation?.Token ?? CancellationToken.None).Detach();
            },
            DispatcherPriority.Background);
    }

    private async Task FillViewportAsync(CancellationToken cancellationToken)
    {
        if (_isFilling || AssociatedObject is not { } scrollViewer || Loader is not { } loader ||
            !IsEnabled || !loader.HasMoreItems || !IsNearEnd(scrollViewer))
        {
            return;
        }

        _isFilling = true;
        _reevaluateAfterFill = false;
        try
        {
            using var session = loader.BeginLoadSession();
            while (!cancellationToken.IsCancellationRequested &&
                   IsEnabled &&
                   ReferenceEquals(loader, Loader) &&
                   loader.HasMoreItems &&
                   IsNearEnd(scrollViewer))
            {
                var previousExtent = scrollViewer.Extent.Height;
                var result = await session.LoadMoreAsync(GetRequestCount(scrollViewer), cancellationToken);

                // Bindings and item containers update after the source task completes. Reading the
                // extent at background priority gives the pending measure/arrange pass a chance to
                // publish its real viewport state before deciding whether another page is needed.
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken);

                if (!result.HasMoreItems) break;
                if (result.AddedItemCount == 0 && Math.Abs(scrollViewer.Extent.Height - previousExtent) < 0.01)
                {
                    // A loader that reports progress is still possible but cannot currently add an
                    // item must not create a hot layout/load loop.
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A behavior has no application-specific error surface. The loader is expected to own
            // user-facing reporting; tracing here still preserves diagnostics for generic callers.
            Trace.TraceError("Incremental loading failed: {0}", ex);
        }
        finally
        {
            _isFilling = false;
            if (_reevaluateAfterFill)
            {
                _reevaluateAfterFill = false;
                QueueEvaluation();
            }
        }
    }

    private int GetRequestCount(ScrollViewer scrollViewer)
    {
        var pageSize = Math.Max(PageSize, 1);
        var maximum = Math.Max(MaximumLoadCount, pageSize);
        var itemHeight = EstimatedItemHeight;
        if (!double.IsFinite(scrollViewer.Viewport.Height) || !double.IsFinite(scrollViewer.Extent.Height))
        {
            return pageSize;
        }

        var missingHeight = Math.Max(scrollViewer.Viewport.Height - scrollViewer.Extent.Height, 0d);
        if (missingHeight <= 0d || itemHeight <= 0d || !double.IsFinite(itemHeight))
        {
            return pageSize;
        }

        var estimate = Math.Ceiling(missingHeight / itemHeight) + Math.Max(OverscanItemCount, 0);
        if (estimate >= maximum) return maximum;

        return Math.Clamp((int)estimate, pageSize, maximum);
    }

    private bool IsNearEnd(ScrollViewer scrollViewer)
    {
        if (scrollViewer.Viewport.Height <= 0d || !double.IsFinite(scrollViewer.Viewport.Height)) return false;

        var remaining = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        var threshold = double.IsFinite(Threshold) ? Math.Max(Threshold, 0d) : 0d;
        return remaining <= threshold;
    }

    private void ResetLifetime()
    {
        CancelLifetime();
        if (AssociatedObject is not null && IsEnabled)
        {
            _lifetimeCancellation = new CancellationTokenSource();
        }
    }

    private void CancelLifetime()
    {
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;
    }
}