using ShadUI;

namespace Everywhere.Views;

public sealed partial class TransientWindow : ShadWindow
{
    /// <summary>
    /// Defines the <see cref="TitleBarContentOverride"/> property, which allows overriding the default title bar content of the TransientWindow.
    /// </summary>
    public static readonly StyledProperty<object?> TitleBarContentOverrideProperty =
        AvaloniaProperty.Register<TransientWindow, object?>(nameof(TitleBarContentOverride));

    public object? TitleBarContentOverride
    {
        get => GetValue(TitleBarContentOverrideProperty);
        set => SetValue(TitleBarContentOverrideProperty, value);
    }

    public TransientWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Its content should be null before closing to make it detach from the visual tree.
        // Otherwise, it will try to attach to the visual tree again (Exception).
        Content = null;
        base.OnClosed(e);
    }
}