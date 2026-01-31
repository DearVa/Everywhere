using Avalonia.Controls;

namespace Everywhere.Views;

public class ChatMessageItemsControl : ItemsControl
{
    /// <summary>
    /// Defines the <see cref="IsReadonly"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsReadonlyProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(IsReadonly));

    /// <summary>
    /// Gets or sets a value indicating whether the control is in read-only mode.
    /// </summary>
    public bool IsReadonly
    {
        get => GetValue(IsReadonlyProperty);
        set => SetValue(IsReadonlyProperty, value);
    }
}