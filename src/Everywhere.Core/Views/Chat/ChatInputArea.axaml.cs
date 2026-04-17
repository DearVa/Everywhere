using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Disposables;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Views;

[TemplatePart("PART_TextEditor", typeof(TextEditor), IsRequired = true)]
[TemplatePart("PART_SendButton", typeof(Button), IsRequired = true)]
[TemplatePart("PART_ChatAttachmentItemsControl", typeof(ChatAttachmentItemsControl), IsRequired = true)]
[TemplatePart("PART_AssistantSelectionMenuItem", typeof(MenuItem), IsRequired = true)]
public sealed partial class ChatInputArea : TemplatedControl
{
    public static readonly DirectProperty<ChatInputArea, string?> TextProperty = AvaloniaProperty.RegisterDirect<ChatInputArea, string?>(
        nameof(Text),
        o => o.Text,
        (o, v) => o.Text = v);

    public static readonly StyledProperty<int> MaxLengthProperty =
        TextBox.MaxLengthProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<ChatInputArea, string?>(nameof(Watermark));

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

    public static readonly StyledProperty<IEnumerable<CustomAssistant>?> CustomAssistantItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, IEnumerable<CustomAssistant>?>(nameof(CustomAssistantItemsSource));

    public static readonly StyledProperty<CustomAssistant?> SelectedCustomAssistantProperty =
        AvaloniaProperty.Register<ChatInputArea, CustomAssistant?>(nameof(SelectedCustomAssistant));

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> AddChatAttachmentMenuItemsProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(AddChatAttachmentMenuItems),
            o => o.AddChatAttachmentMenuItems);

    public static readonly StyledProperty<bool> IsToolCallSupportedProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallSupported));

    public static readonly StyledProperty<bool> IsToolCallEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallEnabled));

    public static readonly StyledProperty<Flyout?> ToolCallButtonFlyoutProperty =
        AvaloniaProperty.Register<ChatInputArea, Flyout?>(nameof(ToolCallButtonFlyout));

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> SettingsMenuItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(SettingsMenuItemsSource),
            o => o.SettingsMenuItemsSource);

    public static readonly StyledProperty<ICloudClient?> CloudClientProperty =
        AvaloniaProperty.Register<ChatInputArea, ICloudClient?>(nameof(CloudClient));

    public static readonly StyledProperty<bool> IsSendButtonEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsSendButtonEnabled), true);

    public static readonly StyledProperty<object?> LeadingContentProperty =
        AvaloniaProperty.Register<ChatInputArea, object?>(nameof(LeadingContent));

    public static readonly StyledProperty<IDataTemplate?> LeadingContentTemplateProperty =
        AvaloniaProperty.Register<ChatInputArea, IDataTemplate?>(nameof(LeadingContentTemplate));

    public string? Text
    {
        get => _text;
        set
        {
            if (_isTextChanging) return;
            if (!SetAndRaise(TextProperty, ref _text, value)) return;

            _isTextChanging = true;
            try
            {
                var hasLeading = LeadingContent != null;
                _textEditor?.Text = hasLeading ? "\uFFFC" + value : value;
                RaisePropertyChanged(TextProperty, value, null);
            }
            finally
            {
                _isTextChanging = false;
            }
        }
    }

    public string SelectedText
    {
        set => _textEditor?.SelectedText = value;
    }

    public int MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

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

    public IEnumerable<CustomAssistant>? CustomAssistantItemsSource
    {
        get => GetValue(CustomAssistantItemsSourceProperty);
        set => SetValue(CustomAssistantItemsSourceProperty, value);
    }

    public IEnumerable? AddChatAttachmentMenuItems
    {
        get;
        set => SetAndRaise(AddChatAttachmentMenuItemsProperty, ref field, value);
    } = new AvaloniaList<MenuItem>();

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

    public Flyout? ToolCallButtonFlyout
    {
        get => GetValue(ToolCallButtonFlyoutProperty);
        set => SetValue(ToolCallButtonFlyoutProperty, value);
    }

    public IEnumerable? SettingsMenuItemsSource
    {
        get;
        set => SetAndRaise(SettingsMenuItemsSourceProperty, ref field, value);
    } = new AvaloniaList<object>();

    public ICloudClient? CloudClient
    {
        get => GetValue(CloudClientProperty);
        set => SetValue(CloudClientProperty, value);
    }

    public bool IsSendButtonEnabled
    {
        get => GetValue(IsSendButtonEnabledProperty);
        set => SetValue(IsSendButtonEnabledProperty, value);
    }

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public IDataTemplate? LeadingContentTemplate
    {
        get => GetValue(LeadingContentTemplateProperty);
        set => SetValue(LeadingContentTemplateProperty, value);
    }

    private IDisposable? _textChangedSubscription;
    private IDisposable? _sendButtonClickSubscription;
    private IDisposable? _textPresenterSizeChangedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerMovedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerExitedSubscription;
    private IDisposable? _assistantSelectionMenuItemPointerWheelChangedSubscription;
    private ChatAttachmentItemsControl? _chatAttachmentItemsControl;
    private TextEditor? _textEditor;
    private bool _isTextChanging;
    private string? _text;

    private readonly OverlayWindow _visualElementAttachmentOverlayWindow = new()
    {
        Content = new Border
        {
            Background = Brushes.DodgerBlue,
            Opacity = 0.2
        },
    };

    public ChatInputArea()
    {
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PreeditChangedEventRegistry.PreeditChangedEvent, HandlePreeditChanged, RoutingStrategies.Bubble);
    }

    public bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center)
    {
        center = default;
        return _chatAttachmentItemsControl?.TryGetAttachmentCenterOnScreen(attachment, out center) ?? false;
    }

    private void HandleTextEditorTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextEditor textEditor) return;

        if (LeadingContent != null)
        {
            var document = textEditor.Document;
            if (document == null || document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC')
            {
                LeadingContent = null;
            }
        }

        _isTextChanging = true;
        try
        {
            SetAndRaise(TextProperty, ref _text, textEditor.Text?.TrimStart('\uFFFC'));
            RaiseEvent(new TextChangedEventArgs(TextBox.TextChangedEvent, this)); // For ChatWindow Handling
        }
        finally
        {
            _isTextChanging = false;
        }
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

        // Remove selection event if previous exists
        if (_textEditor != null)
        {
            _textEditor.TextArea.TextView.ElementGenerators.Clear();
            _textEditor.TextArea.TextView.BackgroundRenderers.Clear();
        }

        _textEditor = e.NameScope.Find<TextEditor>("PART_TextEditor").NotNull();
        _textEditor.Text = _text;
        _textEditor.TextArea.TextView.ElementGenerators.Add(new LeadingContentElementGenerator(this, _textEditor));
        _textEditor.TextArea.TextView.BackgroundRenderers.Add(new WatermarkRenderer(this, _textEditor));

        _textEditor.TextChanged += HandleTextEditorTextChanged;
        _textChangedSubscription = Disposable.Create(() => _textEditor.TextChanged -= HandleTextEditorTextChanged);

        _textEditor.AddDisposableHandler(
            KeyDownEvent,
            HandleTextEditorKeyDownClipboard,
            RoutingStrategies.Tunnel);

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

        _chatAttachmentItemsControl = e.NameScope.Find<ChatAttachmentItemsControl>("PART_ChatAttachmentItemsControl").NotNull();
        _chatAttachmentItemsControlPointerMovedSubscription = _chatAttachmentItemsControl.AddDisposableHandler(
            PointerMovedEvent,
            (_, args) =>
            {
                var element = args.Source as StyledElement;
                while (element != null)
                {
                    element = element.Parent;
                    if (element is not { DataContext: VisualElementAttachment attachment }) continue;
                    _visualElementAttachmentOverlayWindow.UpdateForVisualElement(attachment.Element?.Target);
                    return;
                }
                _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null);
            },
            handledEventsToo: true);
        _chatAttachmentItemsControlPointerExitedSubscription = _chatAttachmentItemsControl.AddDisposableHandler(
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

        if (change.Property == LeadingContentProperty && _textEditor != null)
        {
            var hasLeading = change.NewValue != null;
            var document = _textEditor.Document;
            if (document == null) return;

            if (hasLeading)
            {
                if (document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC')
                {
                    document.Insert(0, "\uFFFC");
                }
                else
                {
                    // \uFFFC is already present, just redraw visual lines to recreate the inline object with new context
                    _textEditor.TextArea.TextView.Redraw();
                }
            }
            else
            {
                if (document.TextLength > 0 && document.GetCharAt(0) == '\uFFFC')
                {
                    document.Remove(0, 1);
                }
            }
        }
        else if (change.Property == WatermarkProperty && _textEditor != null)
        {
            _textEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        else if (change.Property == ChatAttachmentItemsSourceProperty)
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

    public void Focus()
    {
        _textEditor?.TextArea.Focus();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _textEditor?.SelectionStart = _textEditor.Document.TextLength;
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
        var assistants = CustomAssistantItemsSource?.ToList();
        if (assistants is null || assistants.Count <= 1) return;

        var currentIndex = SelectedCustomAssistant is not null ? assistants.IndexOf(SelectedCustomAssistant) : -1;
        if (currentIndex == -1)
        {
            SelectedCustomAssistant = assistants[0];
            e.Handled = true;
            return;
        }

        currentIndex = e.Delta.Y switch
        {
            > 0 => Math.Max(currentIndex - 1, 0),
            < 0 => Math.Min(currentIndex + 1, assistants.Count - 1),
            _ => currentIndex
        };

        SelectedCustomAssistant = assistants[currentIndex];
        e.Handled = true;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                Key.D0 => 9,
                _ => -1
            };

            if (index >= 0 && CustomAssistantItemsSource != null)
            {
                var assistant = CustomAssistantItemsSource.ElementAtOrDefault(index);
                if (assistant != null)
                {
                    SelectedCustomAssistant = assistant;
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.V)
            {
                var pastingEvent = new RoutedEventArgs(TextBox.PastingFromClipboardEvent, this);
                RaiseEvent(pastingEvent);
                e.Handled = pastingEvent.Handled;
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

    public async void Copy()
    {
        try
        {
            if (_textEditor is not { } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var selectionText = textEditor.SelectedText.TrimStart('\uFFFC');
            if (!string.IsNullOrEmpty(selectionText))
            {
                await clipboard.SetTextAsync(selectionText);
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatInputArea>().Information(ex, "Failed to copy");
        }
    }

    public async void Cut()
    {
        try
        {
            if (_textEditor is not { } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var selectionText = textEditor.SelectedText.TrimStart('\uFFFC');
            var start = textEditor.SelectionStart;
            var length = textEditor.SelectionLength;
            textEditor.Document.Remove(start, length);

            if (!string.IsNullOrEmpty(selectionText))
            {
                await clipboard.SetTextAsync(selectionText);
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatInputArea>().Information(ex, "Failed to cut");
        }
    }

    public async void Paste()
    {
        try
        {
            if (_textEditor is not { Document: { } document, TextArea: { } textArea } textEditor) return;

            var clipboard = TopLevel.GetTopLevel(textEditor)?.Clipboard;
            if (clipboard is null) return;

            var pastingEvent = new RoutedEventArgs(TextBox.PastingFromClipboardEvent, this);
            RaiseEvent(pastingEvent);
            if (pastingEvent.Handled) return;

            document.BeginUpdate();
            try
            {
                var text = await clipboard.TryGetTextAsync();
                if (text.IsNullOrEmpty()) return;

                text = text.Replace("\uFFFC", string.Empty);
                if (!string.IsNullOrEmpty(text))
                {
                    textArea.Selection.ReplaceSelectionWithText(text);
                }

                textArea.Caret.BringCaretToView();
            }
            catch (OutOfMemoryException) { } // May happen when pasting huge text
            finally
            {
                textArea.Document.EndUpdate();
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ChatInputArea>().Information(ex, "Failed to paste");
        }
    }

    private static void HandleTextEditorKeyDownClipboard(object? sender, KeyEventArgs e)
    {
        if (sender is not TextEditor textEditor) return;

        switch (e.Key)
        {
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanPaste:
            {
                textEditor.Paste();
                e.Handled = true;
                break;
            }
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanCopy:
            {
                textEditor.Copy();
                e.Handled = true;
                break;
            }
            case Key.X when e.KeyModifiers.HasFlag(KeyModifiers.Control) && textEditor.CanCut:
            {
                textEditor.Cut();
                e.Handled = true;
                break;
            }
        }
    }

    // AvaloniaEdit does not support preedit, so this is a workaround for it.
    // I use harmony to patch TextAreaTextInputMethodClient, and raise PreeditChangedEvent when the preedit text changes.
    // Then I handle this event in ChatInputArea and update the PreeditText and PreeditRect properties accordingly.
    // https://github.com/AvaloniaUI/AvaloniaEdit/pull/532

    #region Preedit

    public static readonly DirectProperty<ChatInputArea, string?> PreeditTextProperty = AvaloniaProperty.RegisterDirect<ChatInputArea, string?>(
        nameof(PreeditText),
        o => o.PreeditText);

    public static readonly DirectProperty<ChatInputArea, Rect> PreeditRectProperty = AvaloniaProperty.RegisterDirect<ChatInputArea, Rect>(
        nameof(PreeditRect),
        o => o.PreeditRect);

    public string? PreeditText
    {
        get;
        private set => SetAndRaise(PreeditTextProperty, ref field, value);
    }

    public Rect PreeditRect
    {
        get;
        private set => SetAndRaise(PreeditRectProperty, ref field, value);
    }

    private void HandlePreeditChanged(object? sender, PreeditChangedEventArgs e)
    {
        PreeditText = e.PreeditText;
        PreeditRect = e.CursorRectangle;
    }

    #endregion

}

file class LeadingContentElementGenerator(ChatInputArea inputArea, TextEditor textEditor) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (startOffset > 0) return -1;
        if (inputArea.LeadingContent == null) return -1;
        var document = textEditor.Document;
        if (document is { TextLength: > 0 } && document.GetCharAt(0) == '\uFFFC') return 0;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (offset != 0 || inputArea.LeadingContent == null) return null;
        var document = textEditor.Document;
        if (document == null || document.TextLength == 0 || document.GetCharAt(0) != '\uFFFC') return null;

        var contentControl = new ContentControl
        {
            Content = inputArea.LeadingContent,
            ContentTemplate = inputArea.LeadingContentTemplate,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0) // Add some spacing between the leading content and the text
        };

        return new CenteredInlineObjectElement(1, contentControl);
    }
}

file class CenteredInlineObjectElement(int documentLength, Control element) : InlineObjectElement(documentLength, element)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        return new CenteredInlineObjectRun(1, TextRunProperties, Element);
    }
}

file class CenteredInlineObjectRun(int length, TextRunProperties? properties, Control element)
    : InlineObjectRun(length, properties, element)
{
    public override double Baseline
    {
        get
        {
            var defaultBaseline = base.Baseline;
            if (!double.IsNaN(defaultBaseline) && Math.Abs(TextBlock.GetBaselineOffset(Element) - defaultBaseline) > 0.1)
            {
                return defaultBaseline; // Use explicit baseline if defined
            }

            var controlHeight = Size.Height;
            var fontSize = Properties?.FontRenderingEmSize ?? 14.0;
            // Center the control over the text baseline
            return controlHeight / 2 + (fontSize * 0.3);
        }
    }
}

file class WatermarkRenderer(ChatInputArea inputArea, TextEditor textEditor) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        var watermark = inputArea.Watermark;
        if (string.IsNullOrEmpty(watermark)) return;

        var document = textEditor.Document;
        if (document == null) return;
        if ((document.TextLength == 1 && document.GetCharAt(0) == '\uFFFC') || document.TextLength == 0)
        {
            var typeface = new Typeface(textEditor.FontFamily, textEditor.FontStyle, textEditor.FontWeight, textEditor.FontStretch);
            var formattedText = new FormattedText(
                watermark,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                textEditor.FontSize,
                textEditor.Foreground
            );

            double x = 4;
            double y = 0;

            if (textView.VisualLinesValid && textView.VisualLines.Count > 0)
            {
                var line = textView.VisualLines[0];
                var displayPos = line.GetVisualPosition(document.TextLength, VisualYPosition.TextTop);
                x = displayPos.X;
                y = displayPos.Y;
            }

            using var _ = drawingContext.PushOpacity(0.5);
            drawingContext.DrawText(formattedText, new Point(x, y));
        }
    }
}