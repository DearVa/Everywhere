using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Everywhere.AI;

namespace Everywhere.Views;

/// <summary>
/// A control selects <see cref="ModelProviderConfiguratorType"/> for a given <see cref="CustomAssistant"/>
/// </summary>
[TemplatePart(ListBoxPartName, typeof(ListBox), IsRequired = true)]
public class ModelProviderConfiguratorSelector : TemplatedControl
{
    private const string ListBoxPartName = "PART_ListBox";

    public sealed record ConfiguratorModel(
        ModelProviderConfiguratorType Type,
        DynamicResourceKeyBase HeaderKey,
        DynamicResourceKeyBase DescriptionKey,
        IBrush? Background
    );

    public IReadOnlyList<ConfiguratorModel> ConfiguratorModels { get; } =
    [
        new ConfiguratorModel(
            ModelProviderConfiguratorType.PresetBased,
            new DynamicResourceKey(LocaleKey.ModelProviderConfiguratorSelector_PresetBasedConfiguratorModel_Header),
            new DynamicResourceKey(LocaleKey.ModelProviderConfiguratorSelector_PresetBasedConfiguratorModel_Description),
            null),
        new ConfiguratorModel(
            ModelProviderConfiguratorType.Advanced,
            new DynamicResourceKey(LocaleKey.ModelProviderConfiguratorSelector_AdvancedConfiguratorModel_Header),
            new DynamicResourceKey(LocaleKey.ModelProviderConfiguratorSelector_AdvancedConfiguratorModel_Description),
            null),
    ];

    public static readonly DirectProperty<ModelProviderConfiguratorSelector, CustomAssistant?> CustomAssistantProperty =
        AvaloniaProperty.RegisterDirect<ModelProviderConfiguratorSelector, CustomAssistant?>(
        nameof(CustomAssistant),
        o => o.CustomAssistant,
        (o, v) => o.CustomAssistant = v);

    public CustomAssistant? CustomAssistant
    {
        get;
        set
        {
            _isCustomAssistantChanging = true;
            try
            {
                SetAndRaise(CustomAssistantProperty, ref field, value);
                _listBox?.SelectedValue = value?.ConfiguratorType;
            }
            finally
            {
                _isCustomAssistantChanging = false;
            }
        }
    }

    /// <summary>
    /// Defines the <see cref="IsSettingsVisible"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsSettingsVisibleProperty =
        AvaloniaProperty.Register<ModelProviderConfiguratorSelector, bool>(
        nameof(IsSettingsVisible), true);

    /// <summary>
    /// Gets or sets a value indicating whether the settings content is visible.
    /// </summary>
    public bool IsSettingsVisible
    {
        get => GetValue(IsSettingsVisibleProperty);
        set => SetValue(IsSettingsVisibleProperty, value);
    }

    private bool _isCustomAssistantChanging;
    private ListBox? _listBox;
    private IDisposable? _listBoxSelectionChangedSubscription;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _listBoxSelectionChangedSubscription?.Dispose();

        _listBox = e.NameScope.Find<ListBox>(ListBoxPartName);
        _listBoxSelectionChangedSubscription = _listBox?.AddDisposableHandler(SelectingItemsControl.SelectionChangedEvent, HandleSelectionChanged);
    }

    private void HandleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CustomAssistant is not { } customAssistant) return;
        if (_isCustomAssistantChanging) return;

        if (e.RemovedItems is [ConfiguratorModel oldModel, ..])
        {
            customAssistant.GetConfigurator(oldModel.Type).Backup();
        }

        if (e.AddedItems is [ConfiguratorModel newModel, ..])
        {
            customAssistant.GetConfigurator(newModel.Type).Apply();
        }
    }
}