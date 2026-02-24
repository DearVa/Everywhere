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
    public ModelProviderConfigurator Configurator => GetConfigurator(ConfiguratorType);

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
    [HiddenSettingsItem]
    public partial int OutputLimit { get; set; }

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

    public ModelProviderConfigurator GetConfigurator(ModelProviderConfiguratorType type) => type switch
    {
        ModelProviderConfiguratorType.Official => _officialConfigurator,
        ModelProviderConfiguratorType.PresetBased => _presetBasedConfigurator,
        _ => _advancedConfigurator
    };

    public void ApplyTemplate(ModelProviderTemplate? modelProviderTemplate)
    {
        if (modelProviderTemplate is not null)
        {
            Endpoint = modelProviderTemplate.Endpoint;
            Schema = modelProviderTemplate.Schema;
            RequestTimeoutSeconds = modelProviderTemplate.RequestTimeoutSeconds;
        }
        else
        {
            Endpoint = string.Empty;
            Schema = ModelProviderSchema.OpenAI;
            RequestTimeoutSeconds = 20;
        }
    }

    public void ApplyTemplate(ModelDefinitionTemplate? modelDefinitionTemplate)
    {
        if (modelDefinitionTemplate is not null)
        {
            ModelId = modelDefinitionTemplate.ModelId;
            SupportsReasoning = modelDefinitionTemplate.SupportsReasoning;
            SupportsToolCall = modelDefinitionTemplate.SupportsToolCall;
            InputModalities = modelDefinitionTemplate.InputModalities;
            OutputModalities = modelDefinitionTemplate.OutputModalities;
            ContextLimit = modelDefinitionTemplate.ContextLimit;
            OutputLimit = modelDefinitionTemplate.OutputLimit;
        }
        else
        {
            ModelId = string.Empty;
            SupportsReasoning = false;
            SupportsToolCall = false;
            InputModalities = default;
            OutputModalities = default;
            ContextLimit = 0;
            OutputLimit = 0;
        }
    }
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

public abstract class ModelProviderConfigurator : ObservableValidator
{
    [HiddenSettingsItem]
    public abstract SettingsItems SettingsItems { get; }

    /// <summary>
    /// Called before switching to another configurator type to backup necessary values.
    /// </summary>
    public abstract void Backup();

    /// <summary>
    /// Called to apply the configuration to the associated CustomAssistant.
    /// </summary>
    public abstract void Apply();

    /// <summary>
    /// Initializes the configurator by applying the current configuration values.
    /// </summary>
    public void Initialize()
    {
        Backup();
        Apply();
    }

    /// <summary>
    /// Validate the current configuration and show UI feedback if invalid.
    /// </summary>
    /// <returns>
    /// True if the configuration is valid; otherwise, false.
    /// </returns>
    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    /// <summary>
    /// Backups of the original customizable values before switching to advanced configurator.
    /// Key: Property name
    /// Value: (DefaultValue, CustomValue)
    /// </summary>
    private readonly Dictionary<string, object?> _backups = new();

    /// <summary>
    /// When the user switches configurator types, we need to preserve the values set in the advanced configurator.
    /// This method helps to return the original customizable, while keeping a backup if needed.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    protected void Backup<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        _backups[propertyName] = property;
    }

    /// <summary>
    /// Restores the original customizable value if exists in backup, otherwise returns the provided property.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    protected T? Restore<T>(T property, [CallerArgumentExpression("property")] string propertyName = "")
    {
        return _backups.TryGetValue(propertyName, out var backup) ? (T?)backup : property;
    }
}

/// <summary>
/// Configurator for the Everywhere official model provider.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class OfficialModelProviderConfigurator : ModelProviderConfigurator
{
    [DynamicResourceKey("123")]
    public SettingsControl<OfficialModelDefinitionForm> ModelDefinitionForm { get; }

    private readonly CustomAssistant _owner;
    private readonly OfficialModelDefinitionForm _form;

    /// <summary>
    /// Configurator for the Everywhere official model provider.
    /// </summary>
    public OfficialModelProviderConfigurator(CustomAssistant owner)
    {
        _owner = owner;

        ModelDefinitionForm = new SettingsControl<OfficialModelDefinitionForm>(x => new OfficialModelDefinitionForm(x, owner));
        _form = (OfficialModelDefinitionForm)ModelDefinitionForm.CreateControl();
    }

    public override void Backup()
    {
        Backup(_owner.ModelId);
    }

    public override void Apply()
    {
        _owner.ModelProviderTemplateId = null;
        _owner.Endpoint = null;
        _owner.Schema = ModelProviderSchema.Official;
        _owner.RequestTimeoutSeconds = 20;

        _owner.ModelId = Restore(_owner.ModelId);
    }
}

/// <summary>
/// Configurator for preset-based model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class PresetBasedModelProviderConfigurator(CustomAssistant owner) : ModelProviderConfigurator
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

            owner.ApplyTemplate(ModelProviderTemplate);
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

            owner.ApplyTemplate(ModelDefinitionTemplate);

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

    public override void Backup()
    {
        Backup(owner.ApiKey);
        Backup(owner.ModelProviderTemplateId);
        Backup(owner.ModelDefinitionTemplateId);
    }

    public override void Apply()
    {
        owner.ApiKey = Restore(owner.ApiKey);
        owner.ModelProviderTemplateId = Restore(owner.ModelProviderTemplateId);
        owner.ModelDefinitionTemplateId = Restore(owner.ModelDefinitionTemplateId);

        owner.ApplyTemplate(ModelProviderTemplate);
        owner.ApplyTemplate(ModelDefinitionTemplate);
    }
}

/// <summary>
/// Configurator for advanced model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class AdvancedModelProviderConfigurator(CustomAssistant owner) : ModelProviderConfigurator
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
    public SettingsControl<PreviewEndpointTextBox> PreviewEndpointControl => new(
        new PreviewEndpointTextBox
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
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(
        new ModalitiesSelector
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
    /// Maximum number of tokens that the model can output in a single request.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_OutputLimit_Header,
        LocaleKey.CustomAssistant_OutputLimit_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int OutputLimit
    {
        get => owner.OutputLimit;
        set => owner.OutputLimit = value;
    }

    public override void Backup()
    {
        Backup(Endpoint);
        Backup(Schema);
        Backup(ModelId);
        Backup(SupportsToolCall);
        Backup(SupportsReasoning);
        Backup(owner.InputModalities);
        Backup(owner.OutputModalities);
        Backup(ContextLimit);
        Backup(OutputLimit);
    }

    public override void Apply()
    {
        owner.ModelProviderTemplateId = null;
        owner.ModelDefinitionTemplateId = null;

        Endpoint = Restore(Endpoint);
        Schema = Restore(Schema);
        ModelId = Restore(ModelId);
        SupportsToolCall = Restore(SupportsToolCall);
        SupportsReasoning = Restore(SupportsReasoning);
        owner.InputModalities = Restore(owner.InputModalities);
        owner.OutputModalities = Restore(owner.OutputModalities);
        ContextLimit = Restore(ContextLimit);
        OutputLimit = Restore(OutputLimit);
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