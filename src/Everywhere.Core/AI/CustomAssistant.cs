using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.AI;

/// <summary>
/// Allowing users to define and manage their own custom AI assistants.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CustomAssistant : ObservableValidator, IModelDefinition
{
    [HiddenSettingsItem]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ColoredIcon? Icon { get; set; } = new(ColoredIconType.Lucide) { Kind = LucideIconKind.Bot };

    [ObservableProperty]
    [HiddenSettingsItem]
    [MinLength(1)]
    [MaxLength(128)]
    public required partial string Name { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? Description { get; set; }

    [JsonIgnore]
    [DynamicResourceKey(LocaleKey.Empty)]
    public SettingsControl<CustomAssistantInformationForm> InformationForm => new(
        new CustomAssistantInformationForm
        {
            CustomAssistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SystemPrompt_Header,
        LocaleKey.CustomAssistant_SystemPrompt_Description)]
    [SettingsStringItem(IsMultiline = true, MaxLength = 40960, Watermark = Prompts.DefaultSystemPrompt)]
    [DefaultValue(null)]
    public partial string? SystemPrompt { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    [NotifyPropertyChangedFor(nameof(Configurator))]
    public partial ModelProviderConfiguratorType ConfiguratorType { get; set; }

    [JsonIgnore]
    [HiddenSettingsItem]
    public IModelProviderConfigurator Configurator => GetConfigurator(ConfiguratorType);

    [JsonIgnore]
    [DynamicResourceKey(LocaleKey.CustomAssistant_ConfiguratorSelector_Header)]
    public SettingsControl<ModelProviderConfiguratorSelector> ConfiguratorSelector => new(
        new ModelProviderConfiguratorSelector
        {
            CustomAssistant = this
        });

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? Endpoint { get; set; }

    /// <summary>
    /// The GUID of the API key to use for this custom assistant.
    /// Use string? for forward compatibility.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Guid ApiKey { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelProviderTemplateId { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelDefinitionTemplateId { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool SupportsReasoning { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool SupportsToolCall { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Modalities InputModalities { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial Modalities OutputModalities { get; set; }

    [ObservableProperty]
    [HiddenSettingsItem]
    public partial int ContextLimit { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_RequestTimeoutSeconds_Header,
        LocaleKey.CustomAssistant_RequestTimeoutSeconds_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public partial Customizable<int> RequestTimeoutSeconds { get; set; } = 20;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Temperature_Header,
        LocaleKey.CustomAssistant_Temperature_Description)]
    [SettingsDoubleItem(Min = 0.0, Max = 2.0, Step = 0.1)]
    public partial Customizable<double> Temperature { get; set; } = 1.0;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_TopP_Header,
        LocaleKey.CustomAssistant_TopP_Description)]
    [SettingsDoubleItem(Min = 0.0, Max = 1.0, Step = 0.1)]
    public partial Customizable<double> TopP { get; set; } = 0.9;

    private readonly OfficialModelProviderConfigurator _officialConfigurator;
    private readonly PresetBasedModelProviderConfigurator _presetBasedConfigurator;
    private readonly AdvancedModelProviderConfigurator _advancedConfigurator;

    public CustomAssistant()
    {
        _officialConfigurator = new OfficialModelProviderConfigurator(this);
        _presetBasedConfigurator = new PresetBasedModelProviderConfigurator(this);
        _advancedConfigurator = new AdvancedModelProviderConfigurator(this);
    }

    public IModelProviderConfigurator GetConfigurator(ModelProviderConfiguratorType type) => type switch
    {
        ModelProviderConfiguratorType.Official => _officialConfigurator,
        ModelProviderConfiguratorType.PresetBased => _presetBasedConfigurator,
        _ => _advancedConfigurator
    };
}

public enum ModelProviderConfiguratorType
{
    /// <summary>
    /// Advanced first for forward compatibility.
    /// </summary>
    Advanced,
    PresetBased,
    Official,
}

public interface IModelProviderConfigurator
{
    [HiddenSettingsItem]
    SettingsItems SettingsItems { get; }

    /// <summary>
    /// Called before switching to another configurator type to backup necessary values.
    /// </summary>
    void Backup();

    /// <summary>
    /// Called to apply the configuration to the associated CustomAssistant.
    /// </summary>
    void Apply();

    /// <summary>
    /// Validate the current configuration and show UI feedback if invalid.
    /// </summary>
    /// <returns>
    /// True if the configuration is valid; otherwise, false.
    /// </returns>
    bool Validate();
}

/// <summary>
/// Configurator for the Everywhere official model provider.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class OfficialModelProviderConfigurator(CustomAssistant owner) : ObservableValidator, IModelProviderConfigurator
{
    [DynamicResourceKey("123")]
    public SettingsControl<OfficialModelDefinitionForm> ModelDefinitionForm { get; } = new();

    public void Backup()
    {
    }

    public void Apply()
    {
        owner.Endpoint = null;
        owner.Schema = ModelProviderSchema.Official;
        owner.RequestTimeoutSeconds = 20;
    }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }
}

/// <summary>
/// Configurator for preset-based model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class PresetBasedModelProviderConfigurator(CustomAssistant owner) : ObservableValidator, IModelProviderConfigurator
{
    /// <summary>
    /// The ID of the model provider to use for this custom assistant.
    /// This ID should correspond to one of the available model providers in the application.
    /// </summary>
    [HiddenSettingsItem]
    public string? ModelProviderTemplateId
    {
        get => owner.ModelProviderTemplateId;
        set
        {
            if (value == owner.ModelProviderTemplateId) return;
            owner.ModelProviderTemplateId = value;

            ApplyModelProvider();
            ModelDefinitionTemplateId = null;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelProviderTemplate));
            OnPropertyChanged(nameof(ModelDefinitionTemplates));
        }
    }

    [Required]
    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsSelectionItem(nameof(ModelProviderTemplates), DataTemplateKey = typeof(ModelProviderTemplate))]
    public ModelProviderTemplate? ModelProviderTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId);
        set => ModelProviderTemplateId = value?.Id;
    }

    [HiddenSettingsItem]
    public Guid ApiKey
    {
        get => owner.ApiKey;
        set
        {
            if (owner.ApiKey == value) return;

            owner.ApiKey = value;
            _apiKeyBackup = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ServiceLocator.Resolve<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
            [!ApiKeyComboBox.DefaultNameProperty] = new Binding($"{nameof(ModelProviderTemplate)}.{nameof(ModelProviderTemplate.DisplayName)}")
            {
                Source = this,
            },
        });

    [JsonIgnore]
    [HiddenSettingsItem]
    private IEnumerable<ModelDefinitionTemplate> ModelDefinitionTemplates => ModelProviderTemplate?.ModelDefinitions ?? [];

    [HiddenSettingsItem]
    public string? ModelDefinitionTemplateId
    {
        get => owner.ModelDefinitionTemplateId;
        set
        {
            if (value == owner.ModelDefinitionTemplateId) return;
            owner.ModelDefinitionTemplateId = value;

            ApplyModelDefinition();

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelDefinitionTemplate));
        }
    }

    [Required]
    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
    [SettingsSelectionItem(nameof(ModelDefinitionTemplates), DataTemplateKey = typeof(ModelDefinitionTemplate))]
    public ModelDefinitionTemplate? ModelDefinitionTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId)?
            .ModelDefinitions.FirstOrDefault(m => m.ModelId == ModelDefinitionTemplateId);
        set => ModelDefinitionTemplateId = value?.ModelId;
    }

    private Guid _apiKeyBackup;

    public void Backup()
    {
        _apiKeyBackup = owner.ApiKey;
    }

    public void Apply()
    {
        owner.ApiKey = _apiKeyBackup;

        ApplyModelProvider();
        ApplyModelDefinition();
    }

    private void ApplyModelProvider()
    {
        if (ModelProviderTemplate is { } modelProviderTemplate)
        {
            owner.Endpoint = modelProviderTemplate.Endpoint;
            owner.Schema = modelProviderTemplate.Schema;
            owner.RequestTimeoutSeconds = modelProviderTemplate.RequestTimeoutSeconds;
        }
        else
        {
            owner.Endpoint = string.Empty;
            owner.Schema = ModelProviderSchema.OpenAI;
            owner.RequestTimeoutSeconds = 20;
        }
    }

    private void ApplyModelDefinition()
    {
        if (ModelDefinitionTemplate is { } modelDefinitionTemplate)
        {
            owner.ModelId = modelDefinitionTemplate.ModelId;
            owner.SupportsReasoning = modelDefinitionTemplate.SupportsReasoning;
            owner.SupportsToolCall = modelDefinitionTemplate.SupportsToolCall;
            owner.InputModalities = modelDefinitionTemplate.InputModalities;
            owner.OutputModalities = modelDefinitionTemplate.OutputModalities;
            owner.ContextLimit = modelDefinitionTemplate.ContextLimit;
        }
        else
        {
            owner.ModelId = string.Empty;
            owner.SupportsReasoning = false;
            owner.SupportsToolCall = false;
            owner.InputModalities = default;
            owner.OutputModalities = default;
            owner.ContextLimit = 0;
        }
    }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }
}

/// <summary>
/// Configurator for advanced model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class AdvancedModelProviderConfigurator(CustomAssistant owner) : ObservableValidator, IModelProviderConfigurator
{
    [HiddenSettingsItem]
    [CustomValidation(typeof(AdvancedModelProviderConfigurator), nameof(ValidateEndpoint))]
    public string? Endpoint
    {
        get => owner.Endpoint;
        set
        {
            if (owner.Endpoint == value) return;

            ValidateProperty(value);
            owner.Endpoint = value;
            OnPropertyChanged();
        }
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Endpoint_Header,
        LocaleKey.CustomAssistant_Endpoint_Description)]
    public SettingsControl<PreviewEndpointTextBox> PreviewEndpointControl => new(new PreviewEndpointTextBox
    {
        MinWidth = 320d,
        [!PreviewEndpointTextBox.EndpointProperty] = new Binding(nameof(Endpoint))
        {
            Source = this,
            Mode = BindingMode.TwoWay
        },
        [!PreviewEndpointTextBox.SchemaProperty] = new Binding(nameof(Schema))
        {
            Source = this,
            Mode = BindingMode.OneWay
        }
    });

    [HiddenSettingsItem]
    public Guid ApiKey
    {
        get => owner.ApiKey;
        set
        {
            if (owner.ApiKey == value) return;

            owner.ApiKey = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ServiceLocator.Resolve<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
        });

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Schema_Header,
        LocaleKey.CustomAssistant_Schema_Description)]
    public ModelProviderSchema Schema
    {
        get => owner.Schema;
        set
        {
            if (owner.Schema == value) return;

            owner.Schema = value;
            OnPropertyChanged();
        }
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelId_Header,
        LocaleKey.CustomAssistant_ModelId_Description)]
    [Required, MinLength(1)]
    public string? ModelId
    {
        get => owner.ModelId;
        set => owner.ModelId = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SupportsReasoning_Header,
        LocaleKey.CustomAssistant_SupportsReasoning_Description)]
    public bool SupportsReasoning
    {
        get => owner.SupportsReasoning;
        set => owner.SupportsReasoning = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SupportsToolCall_Header,
        LocaleKey.CustomAssistant_SupportsToolCall_Description)]
    public bool SupportsToolCall
    {
        get => owner.SupportsToolCall;
        set => owner.SupportsToolCall = value;
    }

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_InputModalities_Header,
        LocaleKey.CustomAssistant_InputModalities_Description)]
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(new ModalitiesSelector
    {
        [!ModalitiesSelector.ModalitiesProperty] = new Binding(nameof(owner.InputModalities))
        {
            Source = owner,
            Mode = BindingMode.TwoWay
        }
    });

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ContextLimit_Header,
        LocaleKey.CustomAssistant_ContextLimit_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int ContextLimit
    {
        get => owner.ContextLimit;
        set => owner.ContextLimit = value;
    }

    /// <summary>
    /// Backups of the original customizable values before switching to advanced configurator.
    /// Key: Property name
    /// Value: (DefaultValue, CustomValue)
    /// </summary>
    private readonly Dictionary<string, object?> _backups = new();

    public void Backup()
    {
        Backup(Endpoint);
        Backup(Schema);
        Backup(ModelId);
        Backup(SupportsToolCall);
        Backup(SupportsReasoning);
        Backup(owner.InputModalities);
        Backup(owner.OutputModalities);
        Backup(ContextLimit);
    }

    public void Apply()
    {
        Endpoint = Restore(Endpoint);
        Schema = Restore(Schema);
        ModelId = Restore(ModelId);
        SupportsToolCall = Restore(SupportsToolCall);
        SupportsReasoning = Restore(SupportsReasoning);
        owner.InputModalities = Restore(owner.InputModalities);
        owner.OutputModalities = Restore(owner.OutputModalities);
        ContextLimit = Restore(ContextLimit);
    }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    /// <summary>
    /// When the user switches configurator types, we need to preserve the values set in the advanced configurator.
    /// This method helps to return the original customizable, while keeping a backup if needed.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    private void Backup<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        _backups[propertyName] = property;
    }

    private T? Restore<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        return _backups.TryGetValue(propertyName, out var backup) ? (T?)backup : property;
    }

    public static ValidationResult? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ValidationResult(LocaleResolver.AdvancedModelProviderConfigurator_InvalidEndpoint);
        }

        return ValidationResult.Success;
    }
}