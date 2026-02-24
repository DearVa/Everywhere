using System.Collections.ObjectModel;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Cloud;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views;

public partial class OfficialModelDefinitionForm : TemplatedControl
{
    public static readonly DirectProperty<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ReadOnlyObservableCollection<ModelDefinitionTemplate>>(
            nameof(ItemsSource),
            o => o.ItemsSource);

    public ReadOnlyObservableCollection<ModelDefinitionTemplate> ItemsSource => _officialModelProvider.ModelDefinitions;

    public static readonly DirectProperty<OfficialModelDefinitionForm, ModelDefinitionTemplate?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<OfficialModelDefinitionForm, ModelDefinitionTemplate?>(
        nameof(SelectedItem),
        o => o.SelectedItem,
        (o, v) => o.SelectedItem = v);

    public ModelDefinitionTemplate? SelectedItem
    {
        get;
        set
        {
            if (!SetAndRaise(SelectedItemProperty, ref field, value)) return;

            _customAssistant.ApplyTemplate(value);
        }
    }

    private readonly IOfficialModelProvider _officialModelProvider;
    private readonly CustomAssistant _customAssistant;

    public OfficialModelDefinitionForm(IServiceProvider serviceProvider, CustomAssistant customAssistant)
    {
        _officialModelProvider = serviceProvider.GetRequiredService<IOfficialModelProvider>();
        _customAssistant = customAssistant;

        SelectedItem = _officialModelProvider.ModelDefinitions.FirstOrDefault(m => m.ModelId == customAssistant.ModelId);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await _officialModelProvider.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Handle refresh errors (e.g., show a message to the user)
            Console.Error.WriteLine($"Error refreshing model definitions: {ex}");
        }
    }
}