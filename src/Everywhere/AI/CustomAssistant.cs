using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Lucide.Avalonia;

namespace Everywhere.AI;

/// <summary>
/// Allowing users to define and manage their own custom AI assistants.
/// </summary>
[GeneratedSettingsItems]
public partial class CustomAssistant : ObservableObject
{
    [HiddenSettingsItem]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Icon_Header,
        LocaleKey.CustomAssistant_Icon_Description)]
    public partial ColoredIcon? Icon { get; set; } = new(ColoredIconType.Lucide) { Kind = LucideIconKind.Bot };

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Name_Header,
        LocaleKey.CustomAssistant_Name_Description)]
    [SettingsStringItem(MaxLength = 32)]
    public required partial string Name { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Description_Header,
        LocaleKey.CustomAssistant_Description_Description)]
    [SettingsStringItem(IsMultiline = true, MaxLength = 4096, Height = 80)]
    public partial string? Description { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SystemPrompt_Header,
        LocaleKey.CustomAssistant_SystemPrompt_Description)]
    [SettingsStringItem(IsMultiline = true, MaxLength = 40960)]
    public partial Customizable<string> SystemPrompt { get; set; } = Prompts.DefaultSystemPrompt;

    [JsonIgnore]
    [HiddenSettingsItem]
    private static ModelProviderTemplate[] ModelProviderTemplates => ModelProviderTemplate.SupportedTemplates;

    /// <summary>
    /// The ID of the model provider to use for this custom assistant.
    /// This ID should correspond to one of the available model providers in the application.
    /// </summary>
    /// <remarks>
    /// Setting this will set <see cref="Endpoint"/> and <see cref="Schema"/> to the values from the selected model provider.
    /// </remarks>
    [HiddenSettingsItem]
    public string? ModelProviderTemplateId
    {
        get;
        set
        {
            if (value == field) return;
            field = value;

            if (value is not null &&
                ModelProviderTemplates.FirstOrDefault(t => t.Id == value) is { } template)
            {
                Endpoint.DefaultValue = template.Endpoint;
                Schema.DefaultValue = template.Schema;
                RequestTimeoutSeconds.DefaultValue = template.RequestTimeoutSeconds;
                // Note: We do not set ApiKey here, as it may be different for each custom assistant.

                ModelDefinitionTemplateId = template.ModelDefinitions.FirstOrDefault(m => m.IsDefault)?.Id;
            }
            else
            {
                Endpoint.DefaultValue = string.Empty;
                Schema.DefaultValue = ModelProviderSchema.OpenAI;
                RequestTimeoutSeconds.DefaultValue = 20;
                ModelDefinitionTemplateId = null;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelProviderTemplate));
            OnPropertyChanged(nameof(ModelDefinitionTemplates));
        }
    }

    [JsonIgnore]
    [System.ComponentModel.DefaultValue(null)]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsSelectionItem(nameof(ModelProviderTemplates), DataTemplateKey = typeof(ModelProviderTemplate))]
    public ModelProviderTemplate? ModelProviderTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId);
        set => ModelProviderTemplateId = value?.Id;
    }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Endpoint_Header,
        LocaleKey.CustomAssistant_Endpoint_Description)]
    public partial Customizable<string> Endpoint { get; set; } = string.Empty;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Schema_Header,
        LocaleKey.CustomAssistant_Schema_Description)]
    public partial Customizable<ModelProviderSchema> Schema { get; set; } = ModelProviderSchema.OpenAI;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    [SettingsStringItem(IsPassword = true)]
    public partial string? ApiKey { get; set; }

    [JsonIgnore]
    [HiddenSettingsItem]
    private IEnumerable<ModelDefinitionTemplate> ModelDefinitionTemplates => ModelProviderTemplate?.ModelDefinitions ?? [];

    [HiddenSettingsItem]
    public string? ModelDefinitionTemplateId
    {
        get;
        set
        {
            if (value == field) return;
            field = value;

            if (value is not null &&
                ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId) is { } template &&
                template.ModelDefinitions.FirstOrDefault(m => m.Id == value) is { } modelDefinition)
            {
                ModelId.DefaultValue = modelDefinition.Id;
                IsImageInputSupported.DefaultValue = modelDefinition.IsImageInputSupported;
                IsFunctionCallingSupported.DefaultValue = modelDefinition.IsFunctionCallingSupported;
                IsDeepThinkingSupported.DefaultValue = modelDefinition.IsDeepThinkingSupported;
                MaxTokens.DefaultValue = modelDefinition.MaxTokens;
            }
            else
            {
                ModelId.DefaultValue = string.Empty;
                IsImageInputSupported.DefaultValue = false;
                IsFunctionCallingSupported.DefaultValue = false;
                IsDeepThinkingSupported.DefaultValue = false;
                MaxTokens.DefaultValue = 81920;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelDefinitionTemplate));
        }
    }

    [JsonIgnore]
    [System.ComponentModel.DefaultValue(null)]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
    [SettingsSelectionItem(nameof(ModelDefinitionTemplates), DataTemplateKey = typeof(ModelDefinitionTemplate))]
    public ModelDefinitionTemplate? ModelDefinitionTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId)?
            .ModelDefinitions.FirstOrDefault(m => m.Id == ModelDefinitionTemplateId);
        set => ModelDefinitionTemplateId = value?.Id;
    }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelId_Header,
        LocaleKey.CustomAssistant_ModelId_Description)]
    public partial Customizable<string> ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the model supports image input capabilities.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsImageInputSupported_Header,
        LocaleKey.CustomAssistant_IsImageInputSupported_Description)]
    public partial Customizable<bool> IsImageInputSupported { get; set; } = false;

    /// <summary>
    /// Indicates whether the model supports function calling capabilities.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsFunctionCallingSupported_Header,
        LocaleKey.CustomAssistant_IsFunctionCallingSupported_Description)]
    public partial Customizable<bool> IsFunctionCallingSupported { get; set; } = false;

    /// <summary>
    /// Indicates whether the model supports tool calls.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsDeepThinkingSupported_Header,
        LocaleKey.CustomAssistant_IsDeepThinkingSupported_Description)]
    public partial Customizable<bool> IsDeepThinkingSupported { get; set; } = false;

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// aka, the maximum context length.
    /// </summary>
    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_MaxTokens_Header,
        LocaleKey.CustomAssistant_MaxTokens_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public partial Customizable<int> MaxTokens { get; set; } = 81920;

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

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_PresencePenalty_Header,
        LocaleKey.CustomAssistant_PresencePenalty_Description)]
    [SettingsDoubleItem(Min = -2.0, Max = 2.0, Step = 0.1)]
    public partial Customizable<double> PresencePenalty { get; set; } = 0.0;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_FrequencyPenalty_Header,
        LocaleKey.CustomAssistant_FrequencyPenalty_Description)]
    [SettingsDoubleItem(Min = -2.0, Max = 2.0, Step = 0.1)]
    public partial Customizable<double> FrequencyPenalty { get; set; } = 0.0;
}

[JsonSerializable(typeof(CustomAssistant))]
public partial class CustomAssistantJsonSerializerContext : JsonSerializerContext;