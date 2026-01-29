using Avalonia.Controls.Primitives;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// A form for configuring CustomAssistant information (Icon, Name and Description).
/// </summary>
public class CustomAssistantInformationForm : TemplatedControl
{
    /// <summary>
    /// Defines the <see cref="CustomAssistant"/> property.
    /// </summary>
    public static readonly StyledProperty<CustomAssistant?> CustomAssistantProperty =
        AvaloniaProperty.Register<CustomAssistantInformationForm, CustomAssistant?>(nameof(CustomAssistant));

    /// <summary>
    /// Gets or sets the CustomAssistant to configure.
    /// </summary>
    public CustomAssistant? CustomAssistant
    {
        get => GetValue(CustomAssistantProperty);
        set => SetValue(CustomAssistantProperty, value);
    }
}