using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Everywhere.Views;

public sealed class ToggleBlock : ContentControl
{
    public static readonly StyledProperty<bool?> IsCheckedProperty =
        ToggleButton.IsCheckedProperty.AddOwner<ToggleBlock>();

    public bool? IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public static readonly StyledProperty<object?> FalseContentProperty =
        AvaloniaProperty.Register<ToggleBlock, object?>(nameof(FalseContent));

    public object? FalseContent
    {
        get => GetValue(FalseContentProperty);
        set => SetValue(FalseContentProperty, value);
    }

    public static readonly StyledProperty<object?> TrueContentProperty =
        AvaloniaProperty.Register<ToggleBlock, object?>(nameof(TrueContent));

    public object? TrueContent
    {
        get => GetValue(TrueContentProperty);
        set => SetValue(TrueContentProperty, value);
    }

    public static readonly StyledProperty<object?> NullContentProperty =
        AvaloniaProperty.Register<ToggleBlock, object?>(nameof(NullContent));

    public object? NullContent
    {
        get => GetValue(NullContentProperty);
        set => SetValue(NullContentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsCheckedProperty ||
            change.Property == FalseContentProperty && IsChecked is false ||
            change.Property == TrueContentProperty && IsChecked is true ||
            change.Property == NullContentProperty && IsChecked is null)
        {
            UpdateContent();
        }
    }

    private void UpdateContent()
    {
        switch (IsChecked)
        {
            case true:
                Content = TrueContent;
                break;
            case false:
                Content = FalseContent;
                break;
            case null:
                Content = NullContent;
                break;
        }
    }
}