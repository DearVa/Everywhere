using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using Everywhere.Interactions;
using Everywhere.Views;

namespace Everywhere.Core.Tests.Views;

[TestFixture]
public sealed class VariableHeightVirtualizingStackPanelTests
{
    [AvaloniaTest]
    public void TailChanges_WhenItemsAreAddedAndRemoved_PreserveSpacing()
    {
        using var context = CreateTarget(20, 20);

        context.Items.Add(CreateItem(20));
        context.Window.UpdateLayout();

        Assert.Multiple(() =>
        {
            Assert.That(context.Items[0].Bounds.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(context.Items[1].Bounds.Y, Is.EqualTo(26).Within(0.001));
            Assert.That(context.Items[2].Bounds.Y, Is.EqualTo(52).Within(0.001));
            Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(72).Within(0.001));
        });

        context.Items.RemoveAt(2);
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(46).Within(0.001));
    }

    [AvaloniaTest]
    public void HeightAboveViewport_WhenItGrows_KeepsVisibleItemAnchored()
    {
        using var context = CreateTarget(Enumerable.Repeat(20d, 30).ToArray());
        context.ScrollViewer.Offset = new Vector(0, 200);
        context.Window.UpdateLayout();

        var anchoredItem = context.Items[10];
        var positionBefore = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        context.Items[5].Height = 50;
        context.Window.UpdateLayout();

        var positionAfter = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(context.ScrollViewer.Offset.Y, Is.EqualTo(230).Within(0.001));
            Assert.That(positionAfter, Is.Not.Null);
            Assert.That(positionAfter!.Value.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.001));
        });
    }

    [AvaloniaTest]
    public void ItemsBeforeViewport_WhenPrepended_KeepVisibleItemAnchored()
    {
        using var context = CreateTarget(Enumerable.Repeat(40d, 30).ToArray());
        context.ScrollViewer.Offset = new Vector(0, 240);
        context.Window.UpdateLayout();

        var anchoredItem = GetFirstVisibleItem(context);
        var positionBefore = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        context.Items.Insert(0, CreateItem(180));
        context.Items.Insert(0, CreateItem(24));
        context.Items.Insert(0, CreateItem(320));
        context.Window.UpdateLayout();

        var positionAfter = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(positionAfter, Is.Not.Null);
            Assert.That(positionAfter!.Value.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.001));
        });
    }

    [AvaloniaTest]
    public void LargeBatchBeforeViewport_WhenPrepended_KeepsVisibleItemAnchored()
    {
        var heightPattern = new[] { 48d, 720d, 96d, 280d, 64d, 1080d, 160d, 420d };
        var initialHeights = Enumerable.Range(0, 40).Select(index => heightPattern[index % heightPattern.Length]).ToArray();
        using var context = CreateTarget(initialHeights, estimatedItemHeight: 140, windowHeight: 781.6);
        context.Panel.PreserveViewportOnPrepend = true;
        context.ScrollViewer.Offset = new Vector(0, 700);
        context.Window.UpdateLayout();

        var anchoredItem = GetFirstVisibleItem(context);
        var positionBefore = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        for (var index = 0; index < 24; index++)
            context.Items.Insert(0, CreateItem(heightPattern[index % heightPattern.Length]));
        context.Window.UpdateLayout();

        var positionAfter = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(positionAfter, Is.Not.Null, "The viewport anchor was recycled before Avalonia could apply its correction.");
            Assert.That(positionAfter?.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.001));
        });
    }

    [AvaloniaTest]
    public void ItemsAtViewportTop_WhenPrepended_KeepOriginalFirstItemAnchored()
    {
        using var context = CreateTarget(Enumerable.Repeat(40d, 30).ToArray());
        context.Panel.PreserveViewportOnPrepend = true;
        context.ScrollViewer.Offset = default;
        context.Window.UpdateLayout();

        var anchoredItem = context.Items[0];
        var positionBefore = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        context.Items.Insert(0, CreateItem(180));
        context.Window.UpdateLayout();

        var positionAfter = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(positionAfter, Is.Not.Null);
            Assert.That(positionAfter!.Value.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.101));
        });
    }

    [AvaloniaTest]
    public void ItemsAfterViewport_WhenAppended_DoNotMoveVisibleItem()
    {
        using var context = CreateTarget(Enumerable.Repeat(40d, 30).ToArray());
        context.ScrollViewer.Offset = new Vector(0, 240);
        context.Window.UpdateLayout();

        var anchoredItem = GetFirstVisibleItem(context);
        var positionBefore = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        context.Items.Add(CreateItem(180));
        context.Window.UpdateLayout();

        var positionAfter = anchoredItem.TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(positionAfter, Is.Not.Null);
            Assert.That(positionAfter!.Value.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.001));
        });
    }

    [AvaloniaTest]
    public void TallItemCoveringViewportTop_WhenItsHeightGrows_DoesNotAnchorFollowingItem()
    {
        using var context = CreateTarget(1000, 100, 100);
        context.ScrollViewer.Offset = new Vector(0, 950);
        context.Window.UpdateLayout();

        var positionBefore = context.Items[0].TranslatePoint(default, context.ScrollViewer);
        Assert.That(positionBefore, Is.Not.Null);

        context.Items[0].Height = 1100;
        context.Window.UpdateLayout();

        var positionAfter = context.Items[0].TranslatePoint(default, context.ScrollViewer);
        Assert.Multiple(() =>
        {
            Assert.That(context.ScrollViewer.Offset.Y, Is.EqualTo(950).Within(0.001));
            Assert.That(positionAfter, Is.Not.Null);
            Assert.That(positionAfter!.Value.Y, Is.EqualTo(positionBefore!.Value.Y).Within(0.001));
        });
    }

    [AvaloniaTest]
    public void ScrollUpSlowly_WhenUnmeasuredItemsHaveVeryDifferentHeights_MovesContentByRequestedDelta()
    {
        var heightPattern = new[] { 48d, 720d, 96d, 280d, 64d, 1080d, 160d, 420d };
        var itemHeights = Enumerable.Range(0, 80).Select(index => heightPattern[index % heightPattern.Length]).ToArray();
        using var context = CreateTarget(itemHeights, estimatedItemHeight: 140, windowHeight: 600);

        context.ScrollViewer.Offset = new Vector(0, double.PositiveInfinity);
        SettleLayout(context);

        for (var step = 0; step < 600 && context.ScrollViewer.Offset.Y > 0; step++)
        {
            var anchor = GetFirstVisibleItem(context);
            var positionBefore = anchor.TranslatePoint(default, context.ScrollViewer)!.Value.Y;

            context.Window.MouseWheel(new Point(50, 300), new Vector(0, 1));

            var positionAfter = anchor.TranslatePoint(default, context.ScrollViewer);
            Assert.That(positionAfter, Is.Not.Null, $"Anchor recycled at step {step}.");
            Assert.That(
                positionAfter!.Value.Y - positionBefore,
                Is.InRange(0d, 51d),
                $"Unexpected viewport movement at step {step}; offset is {context.ScrollViewer.Offset.Y}.");
        }
    }

    [AvaloniaTest]
    public void ReplaceHistory_WhenPreviouslyAtEnd_SettlesAtNewEnd()
    {
        using var context = CreateTarget(
            Enumerable.Range(0, 30).Select(index => index % 2 == 0 ? 80d : 360d).ToArray(),
            estimatedItemHeight: 140,
            windowHeight: 600);
        context.ScrollViewer.Offset = new Vector(0, double.PositiveInfinity);
        SettleLayout(context);
        Assert.That(context.ScrollViewer.Offset.Y, Is.EqualTo(GetMaximumOffset(context)).Within(0.001));

        context.Items.Clear();
        foreach (var height in Enumerable.Range(0, 50).Select(index => index % 3 == 0 ? 640d : 72d))
            context.Items.Add(CreateItem(height));

        SettleLayout(context);

        Assert.That(context.ScrollViewer.Offset.Y, Is.EqualTo(GetMaximumOffset(context)).Within(0.001));
    }

    [AvaloniaTest]
    public void ReplaceHistory_WhenConversationChanges_SettlesAtNewEnd()
    {
        using var context = CreateTarget(
            Enumerable.Range(0, 30).Select(index => index % 2 == 0 ? 80d : 360d).ToArray(),
            estimatedItemHeight: 140,
            windowHeight: 600);
        context.ScrollViewer.Offset = new Vector(0, double.PositiveInfinity);
        SettleLayout(context);
        context.ScrollViewer.Offset = new Vector(0, Math.Max(0, GetMaximumOffset(context) - 500));
        SettleLayout(context);
        Assert.That(context.ScrollViewer.Offset.Y, Is.LessThan(GetMaximumOffset(context) - 1));

        context.AutoScroll.ScrollToEndToken = new object();
        context.Items.Clear();
        foreach (var height in Enumerable.Range(0, 50).Select(index => index % 3 == 0 ? 640d : 72d))
            context.Items.Add(CreateItem(height));

        SettleLayout(context);

        Assert.That(context.ScrollViewer.Offset.Y, Is.EqualTo(GetMaximumOffset(context)).Within(0.001));
    }

    [AvaloniaTest]
    public void Measure_WhenItemShrinks_AcceptsCurrentHeight()
    {
        using var context = CreateTarget(100);

        context.Items[0].Height = 20;
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(20).Within(0.001));
    }

    private static TestContext CreateTarget(params double[] itemHeights) =>
        CreateTarget(itemHeights, estimatedItemHeight: 20, windowHeight: 100);

    private static TestContext CreateTarget(
        double[] itemHeights,
        double estimatedItemHeight,
        double windowHeight)
    {
        var panel = new VariableHeightVirtualizingStackPanel
        {
            CacheLength = 1,
            EstimatedItemHeight = estimatedItemHeight,
            Spacing = 6
        };
        var items = new ObservableCollection<Control>(itemHeights.Select(CreateItem));
        var presenter = new ItemsPresenter
        {
            [~ItemsPresenter.ItemsPanelProperty] = new TemplateBinding(ItemsPresenter.ItemsPanelProperty)
        };
        var scrollViewer = new ScrollViewer
        {
            Name = "PART_ScrollViewer",
            Content = presenter,
            Template = new FuncControlTemplate<ScrollViewer>((_, nameScope) =>
                new ScrollContentPresenter
                {
                    Name = "PART_ScrollContentPresenter"
                }.RegisterInNameScope(nameScope))
        };
        var autoScroll = new AutoScrollBehavior();
        Interaction.GetBehaviors(scrollViewer).Add(autoScroll);
        var itemsControl = new ItemsControl
        {
            ItemsSource = items,
            ItemsPanel = new FuncTemplate<Panel?>(() => panel),
            Template = new FuncControlTemplate<ItemsControl>((_, nameScope) =>
                scrollViewer.RegisterInNameScope(nameScope))
        };
        var window = new Window
        {
            Width = 100,
            Height = windowHeight,
            Content = itemsControl,
            WindowDecorations = WindowDecorations.None
        };

        window.Show();
        window.UpdateLayout();
        return new TestContext(window, panel, scrollViewer, autoScroll, items);
    }

    private static Control GetFirstVisibleItem(TestContext context)
    {
        foreach (var item in context.Items)
        {
            if (item.TranslatePoint(default, context.ScrollViewer) is not { } position)
                continue;

            if (position.Y + item.Bounds.Height > 0 && position.Y < context.ScrollViewer.Viewport.Height)
                return item;
        }

        throw new AssertionException("No realized item intersects the viewport.");
    }

    private static double GetMaximumOffset(TestContext context) =>
        Math.Max(0, context.ScrollViewer.Extent.Height - context.ScrollViewer.Viewport.Height);

    private static void SettleLayout(TestContext context)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            context.Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Border CreateItem(double height) => new()
    {
        Width = 100,
        Height = height
    };

    private sealed class TestContext : IDisposable
    {
        public TestContext(
            Window window,
            VariableHeightVirtualizingStackPanel panel,
            ScrollViewer scrollViewer,
            AutoScrollBehavior autoScroll,
            ObservableCollection<Control> items)
        {
            Window = window;
            Panel = panel;
            ScrollViewer = scrollViewer;
            AutoScroll = autoScroll;
            Items = items;
        }

        public Window Window { get; }
        public VariableHeightVirtualizingStackPanel Panel { get; }
        public ScrollViewer ScrollViewer { get; }
        public AutoScrollBehavior AutoScroll { get; }
        public ObservableCollection<Control> Items { get; }

        public void Dispose() => Window.Close();
    }
}
