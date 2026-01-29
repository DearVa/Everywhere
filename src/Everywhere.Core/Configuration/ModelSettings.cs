using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;

namespace Everywhere.Configuration;

public partial class ModelSettings : ObservableObject
{
    [ObservableProperty]
    public partial ObservableCollection<CustomAssistant> CustomAssistants { get; set; } = [];

    [ObservableProperty]
    public partial Guid SelectedCustomAssistantId { get; set; }

    /// <summary>
    /// Gets or sets the currently selected custom assistant via <see cref="SelectedCustomAssistantId"/>.
    /// If the index is invalid, returns the first assistant or null if the list is empty.
    /// Setting this property will update the index accordingly.
    /// </summary>
    [JsonIgnore]
    public CustomAssistant? SelectedCustomAssistant
    {
        get => CustomAssistants.FirstOrDefault(a => a.Id == SelectedCustomAssistantId) ??
            CustomAssistants.FirstOrDefault(); // Default to first assistant if index is invalid.
        set
        {
            SelectedCustomAssistantId = CustomAssistants.FirstOrDefault(a => a == value)?.Id ?? Guid.Empty;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    public partial ObservableCollection<ApiKey> ApiKeys { get; set; } = [];
}