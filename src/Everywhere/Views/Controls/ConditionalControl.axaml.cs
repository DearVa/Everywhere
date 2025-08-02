using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Metadata;
using DocumentFormat.OpenXml.Presentation;

namespace Everywhere.Views;

public class ConditionalControl : TemplatedControl
{

    public static readonly StyledProperty<bool> ConditionProperty =
        AvaloniaProperty.Register<ConditionalControl, bool>(nameof(Condition), false);

    public static readonly DirectProperty<ConditionalControl, object?> ActualContentProperty =
        AvaloniaProperty.RegisterDirect<ConditionalControl, object?>(nameof(ActualContent), x => x.ActualContent);

    static ConditionalControl()
    {
        ConditionProperty.Changed.AddClassHandler<ConditionalControl>((x, e) =>
        {
            x.UpdateActualContent();
        });
    }

    private object? _actualContent;

    [Content]
    public Controls Contents { get; } = new Controls();

    public bool Condition
    {
        get => GetValue(ConditionProperty);
        set => SetValue(ConditionProperty, value);
    }

    public object? ActualContent
    {
        get => _actualContent;
        set => SetAndRaise(ActualContentProperty, ref _actualContent, value);
    }

    public ConditionalControl()
    {
        this.Contents.CollectionChanged += (s, e) =>
        {
            UpdateActualContent();
        };
    }

    private void UpdateActualContent()
    {
        ActualContent = Contents switch
        {
            var contents when contents.Count >= 2 => Condition ? contents[1] : contents[0],
            var contents when contents.Count == 1 => Condition ? contents[0] : null,
            _ => null
        };
    }
}