using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
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
    public void Measure_WhenLargeShrinkRecoversBeforeNextFrame_KeepsPreviousHeight()
    {
        using var context = CreateTarget(100);

        context.Items[0].Height = 20;
        context.Window.UpdateLayout();
        context.Panel.InvalidateMeasure();
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(100).Within(0.001));

        context.Items[0].Height = 100;
        context.Window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(100).Within(0.001));
    }

    [AvaloniaTest]
    public void Measure_WhenLargeShrinkPersistsAcrossNextFrame_AcceptsNewHeight()
    {
        using var context = CreateTarget(100);

        context.Items[0].Height = 20;
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(100).Within(0.001));

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();

        Assert.That(context.Panel.DesiredSize.Height, Is.EqualTo(20).Within(0.001));
    }

    private static TestContext CreateTarget(params double[] itemHeights)
    {
        var panel = new VariableHeightVirtualizingStackPanel
        {
            CacheLength = 1,
            EstimatedItemHeight = 20,
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
            Height = 100,
            Content = itemsControl,
            WindowDecorations = WindowDecorations.None
        };

        window.Show();
        window.UpdateLayout();
        return new TestContext(window, panel, scrollViewer, items);
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
            ObservableCollection<Control> items)
        {
            Window = window;
            Panel = panel;
            ScrollViewer = scrollViewer;
            Items = items;
        }

        public Window Window { get; }
        public VariableHeightVirtualizingStackPanel Panel { get; }
        public ScrollViewer ScrollViewer { get; }
        public ObservableCollection<Control> Items { get; }

        public void Dispose() => Window.Close();
    }
}
