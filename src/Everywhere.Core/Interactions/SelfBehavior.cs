using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

/// <summary>
/// Utility behavior that exposes the associated object as a property for binding purposes.
/// </summary>
public sealed class SelfBehavior : Behavior
{
    public static readonly DirectProperty<SelfBehavior, AvaloniaObject?> SelfProperty =
        AvaloniaProperty.RegisterDirect<SelfBehavior, AvaloniaObject?>(nameof(Self), o => o.Self);

    public AvaloniaObject? Self
    {
        get;
        private set => SetAndRaise(SelfProperty, ref field, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        Self = AssociatedObject;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        Self = null;
    }
}