using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Utilities;

namespace Everywhere.Views;

[TemplatePart("PART_SendButton", typeof(Button), IsRequired = true)]
[TemplatePart("PART_ChatAttachmentItemsControl", typeof(ChatAttachmentItemsControl), IsRequired = true)]
[TemplatePart("PART_AssistantSelectionMenuItem", typeof(MenuItem))]
public partial class ChatInputArea : TextBox
{
    public static readonly StyledProperty<bool> PressCtrlEnterToSendProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(PressCtrlEnterToSend));

    public static readonly StyledProperty<IRelayCommand<string>?> CommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<string>?>(nameof(Command));

    public static readonly StyledProperty<IRelayCommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<ICollection<ChatAttachment>?> ChatAttachmentItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, ICollection<ChatAttachment>?>(nameof(ChatAttachmentItemsSource));

    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveAttachmentCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<ChatAttachment>?>(nameof(RemoveAttachmentCommand));

    public static readonly StyledProperty<int> MaxChatAttachmentCountProperty =
        AvaloniaProperty.Register<ChatInputArea, int>(nameof(MaxChatAttachmentCount));

    public static readonly StyledProperty<IEnumerable<CustomAssistant>?> CustomAssistantsProperty =
        AvaloniaProperty.Register<ChatInputArea, IEnumerable<CustomAssistant>?>(nameof(CustomAssistants));

    public static readonly StyledProperty<CustomAssistant?> SelectedCustomAssistantProperty =
        AvaloniaProperty.Register<ChatInputArea, CustomAssistant?>(nameof(SelectedCustomAssistant));

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> AddChatAttachmentMenuItemsProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(AddChatAttachmentMenuItems),
            o => o.AddChatAttachmentMenuItems);

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> SettingsMenuItemsProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(SettingsMenuItems),
            o => o.SettingsMenuItems);

    public static readonly StyledProperty<bool> IsToolCallSupportedProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallSupported));

    public static readonly StyledProperty<bool> IsToolCallEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallEnabled));

    public static readonly StyledProperty<bool> IsSendButtonEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsSendButtonEnabled), true);

    /// <summary>
    /// If true, pressing Ctrl+Enter will send the message, Enter will break the line.
    /// </summary>
    public bool PressCtrlEnterToSend
    {
        get => GetValue(PressCtrlEnterToSendProperty);
        set => SetValue(PressCtrlEnterToSendProperty, value);
    }

    /// <summary>
    /// When the text is executed, the text will be passed as the parameter.
    /// </summary>
    public IRelayCommand<string>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public IRelayCommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ICollection<ChatAttachment>? ChatAttachmentItemsSource
    {
        get => GetValue(ChatAttachmentItemsSourceProperty);
        set => SetValue(ChatAttachmentItemsSourceProperty, value);
    }

    public IRelayCommand<ChatAttachment>? RemoveAttachmentCommand
    {
        get => GetValue(RemoveAttachmentCommandProperty);
        set => SetValue(RemoveAttachmentCommandProperty, value);
    }

    public int MaxChatAttachmentCount
    {
        get => GetValue(MaxChatAttachmentCountProperty);
        set => SetValue(MaxChatAttachmentCountProperty, value);
    }

    public CustomAssistant? SelectedCustomAssistant
    {
        get => GetValue(SelectedCustomAssistantProperty);
        set => SetValue(SelectedCustomAssistantProperty, value);
    }

    public IEnumerable<CustomAssistant>? CustomAssistants
    {
        get => GetValue(CustomAssistantsProperty);
        set => SetValue(CustomAssistantsProperty, value);
    }

    public IEnumerable? AddChatAttachmentMenuItems
    {
        get;
        set => SetAndRaise(AddChatAttachmentMenuItemsProperty, ref field, value);
    } = new AvaloniaList<MenuItem>();

    public IEnumerable? SettingsMenuItems
    {
        get;
        set => SetAndRaise(SettingsMenuItemsProperty, ref field, value);
    } = new AvaloniaList<object>();

    public bool IsToolCallSupported
    {
        get => GetValue(IsToolCallSupportedProperty);
        set => SetValue(IsToolCallSupportedProperty, value);
    }

    public bool IsToolCallEnabled
    {
        get => GetValue(IsToolCallEnabledProperty);
        set => SetValue(IsToolCallEnabledProperty, value);
    }

    public bool IsSendButtonEnabled
    {
        get => GetValue(IsSendButtonEnabledProperty);
        set => SetValue(IsSendButtonEnabledProperty, value);
    }

    private IDisposable? _textChangedSubscription;
    private IDisposable? _sendButtonClickSubscription;
    private IDisposable? _textPresenterSizeChangedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerMovedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerExitedSubscription;
    private IDisposable? _assistantSelectionMenuItemPointerWheelChangedSubscription;

    private readonly OverlayWindow _visualElementAttachmentOverlayWindow = new()
    {
        Content = new Border
        {
            Background = Brushes.DodgerBlue,
            Opacity = 0.2
        },
    };

    static ChatInputArea()
    {
        TextProperty.OverrideDefaultValue<ChatInputArea>(string.Empty);
    }

    public ChatInputArea()
    {
        this.AddDisposableHandler(KeyDownEvent, HandleTextBoxKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeCollector.DisposeToDefault(ref _textChangedSubscription);
        DisposeCollector.DisposeToDefault(ref _sendButtonClickSubscription);
        DisposeCollector.DisposeToDefault(ref _textPresenterSizeChangedSubscription);
        DisposeCollector.DisposeToDefault(ref _chatAttachmentItemsControlPointerMovedSubscription);
        DisposeCollector.DisposeToDefault(ref _chatAttachmentItemsControlPointerExitedSubscription);
        DisposeCollector.DisposeToDefault(ref _assistantSelectionMenuItemPointerWheelChangedSubscription);

        // We handle the click event of the SendButton here instead of using Command binding,
        // because we need to clear the text after sending the message.
        var sendButton = e.NameScope.Find<Button>("PART_SendButton").NotNull();
        _sendButtonClickSubscription = sendButton.AddDisposableHandler(
            Button.ClickEvent,
            (_, args) =>
            {
                if (Command?.CanExecute(Text) is not true) return;
                Command.Execute(Text);
                Text = string.Empty;
                args.Handled = true;
            },
            handledEventsToo: true);

        var chatAttachmentItemsControl = e.NameScope.Find<ChatAttachmentItemsControl>("PART_ChatAttachmentItemsControl").NotNull();
        _chatAttachmentItemsControlPointerMovedSubscription = chatAttachmentItemsControl.AddDisposableHandler(
            PointerMovedEvent,
            (_, args) =>
            {
                var element = args.Source as StyledElement;
                while (element != null)
                {
                    element = element.Parent;
                    if (element is not { DataContext: ChatVisualElementAttachment attachment }) continue;
                    _visualElementAttachmentOverlayWindow.UpdateForVisualElement(attachment.Element?.Target);
                    return;
                }
                _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null);
            },
            handledEventsToo: true);
        _chatAttachmentItemsControlPointerExitedSubscription = chatAttachmentItemsControl.AddDisposableHandler(
            PointerExitedEvent,
            (_, _) => _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null),
            handledEventsToo: true);

        var assistantSelectionMenuItem = e.NameScope.Find<MenuItem>("PART_AssistantSelectionMenuItem");
        if (assistantSelectionMenuItem != null)
        {
            _assistantSelectionMenuItemPointerWheelChangedSubscription = assistantSelectionMenuItem.AddDisposableHandler(
                PointerWheelChangedEvent,
                HandleAssistantSelectionPointerWheelChanged,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChatAttachmentItemsSourceProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldValue)
            {
                oldValue.CollectionChanged -= HandleChatAttachmentItemsSourceChanged;
            }
            if (change.NewValue is INotifyCollectionChanged newValue)
            {
                newValue.CollectionChanged += HandleChatAttachmentItemsSourceChanged;
            }
        }
    }

    private void HandleChatAttachmentItemsSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null); // Hide the overlay window when the attachment list changes.
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null); // Hide the overlay window when the control is unloaded.
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Because this control is inherited from TextBox, it will receive pointer events and broke the MenuItem's pointer events.
        // We need to ignore pointer events if the source is a StyledElement that is inside a MenuItem.
        if (e.Source is StyledElement element && element.FindLogicalAncestorOfType<MenuItem>() != null)
        {
            return;
        }

        base.OnPointerPressed(e);
    }

    [RelayCommand]
    private void SetSelectedCustomAssistant(MenuItem? sender)
    {
        SelectedCustomAssistant = sender?.DataContext as CustomAssistant;
    }

    private void HandleAssistantSelectionPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var assistants = CustomAssistants?.ToList();
        if (assistants is null || assistants.Count <= 1) return;

        var currentIndex = SelectedCustomAssistant is not null
            ? assistants.IndexOf(SelectedCustomAssistant)
            : -1;

        if (currentIndex == -1)
        {
            SelectedCustomAssistant = assistants[0];
            e.Handled = true;
            return;
        }

        currentIndex = e.Delta.Y switch
        {
            // Up
            > 0 => (currentIndex - 1 + assistants.Count) % assistants.Count,
            // Down
            < 0 => (currentIndex + 1) % assistants.Count,
            _ => currentIndex
        };

        SelectedCustomAssistant = assistants[currentIndex];
        e.Handled = true;
    }

    private void HandleTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                Key.D0 => 9,
                _ => -1
            };

            if (index >= 0 && CustomAssistants != null)
            {
                var assistant = CustomAssistants.ElementAtOrDefault(index);
                if (assistant != null)
                {
                    SelectedCustomAssistant = assistant;
                    e.Handled = true;
                    return;
                }
            }
        }

        switch (e.Key)
        {
            case Key.Enter:
            {
                if ((!PressCtrlEnterToSend || e.KeyModifiers != KeyModifiers.Control) &&
                    (PressCtrlEnterToSend || e.KeyModifiers != KeyModifiers.None)) return;

                if (Command?.CanExecute(Text) is not true) break;

                Command.Execute(Text);
                Text = string.Empty;
                e.Handled = true;
                break;
            }
        }
    }
}