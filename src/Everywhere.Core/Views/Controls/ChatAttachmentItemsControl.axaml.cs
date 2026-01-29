using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;

namespace Everywhere.Views;

public class ChatAttachmentItemsControl : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        ItemsControl.ItemsSourceProperty.AddOwner<ChatAttachmentItemsControl>();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RemoveCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChatAttachmentItemsControl, IRelayCommand<ChatAttachment>?>(
            nameof(RemoveCommand));

    /// <summary>
    /// Gets or sets the command to remove an attachment.
    /// </summary>
    public IRelayCommand<ChatAttachment>? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }
}