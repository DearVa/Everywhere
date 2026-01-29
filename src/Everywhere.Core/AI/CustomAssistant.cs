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
public partial class CustomAssistant : ObservableValidator
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

    /// <summary>
    /// Indicates whether the model supports image input capabilities.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool IsImageInputSupported { get; set; }

    /// <summary>
    /// Indicates whether the model supports function calling capabilities.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool IsFunctionCallingSupported { get; set; }

    /// <summary>
    /// Indicates whether the model supports tool calls.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial bool IsDeepThinkingSupported { get; set; }

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// aka, the maximum context length.
    /// </summary>
    [ObservableProperty]
    [HiddenSettingsItem]
    public partial int MaxTokens { get; set; }

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
    public void Backup()
    {
    }

    public void Apply()
    {
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
    /// Helper property to get all supported model provider templates.
    /// </summary>
    [JsonIgnore]
    [HiddenSettingsItem]
    private static ModelProviderTemplate[] ModelProviderTemplates { get; } = [
        new()
        {
            Id = "openai",
            DisplayName = "OpenAI",
            Endpoint = "https://api.openai.com/v1",
            OfficialWebsiteUrl = "https://openai.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/openai-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/openai-light.svg",
            Schema = ModelProviderSchema.OpenAIResponses,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "gpt-5.2",
                    ModelId = "gpt-5.2",
                    DisplayName = "GPT-5.2",
                    MaxTokens = 400_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-5.1",
                    ModelId = "gpt-5.1",
                    DisplayName = "GPT-5.1",
                    MaxTokens = 400_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-5",
                    ModelId = "gpt-5",
                    DisplayName = "GPT-5",
                    MaxTokens = 400_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-5-mini",
                    ModelId = "gpt-5-mini",
                    DisplayName = "GPT-5 mini",
                    MaxTokens = 400_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "o4-mini",
                    ModelId = "o4-mini",
                    DisplayName = "o4-mini",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-4.1",
                    ModelId = "gpt-4.1",
                    DisplayName = "GPT 4.1",
                    MaxTokens = 1_000_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-4.1-mini",
                    ModelId = "gpt-4.1-mini",
                    DisplayName = "GPT 4.1 mini",
                    MaxTokens = 1_000_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gpt-4o",
                    ModelId = "gpt-4o",
                    DisplayName = "GPT-4o",
                    MaxTokens = 128_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                }
            ]
        },
        new()
        {
            Id = "anthropic",
            DisplayName = "Anthropic (Claude)",
            Endpoint = "https://api.anthropic.com",
            OfficialWebsiteUrl = "https://www.anthropic.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/anthropic-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/anthropic-light.svg",
            Schema = ModelProviderSchema.Anthropic,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "claude-opus-4-5-20251101",
                    ModelId = "claude-opus-4-5-20251101",
                    DisplayName = "Claude Opus 4.5",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-sonnet-4-5-20250929",
                    ModelId = "claude-sonnet-4-5-20250929",
                    DisplayName = "Claude Sonnet 4.5",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-haiku-4-5-20251001",
                    ModelId = "claude-haiku-4-5-20251001",
                    DisplayName = "Claude Haiku 4.5",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-opus-4-1-20250805",
                    ModelId = "claude-opus-4-1-20250805",
                    DisplayName = "Claude Opus 4.1",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-opus-4-20250514",
                    ModelId = "claude-opus-4-20250514",
                    DisplayName = "Claude Opus 4",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-sonnet-4-20250514",
                    ModelId = "claude-sonnet-4-20250514",
                    DisplayName = "Claude Sonnet 4",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-3-7-sonnet-20250219",
                    ModelId = "claude-3-7-sonnet-20250219",
                    DisplayName = "Claude 3.7 Sonnet",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "claude-3-5-haiku-20241022",
                    ModelId = "claude-3-5-haiku-20241022",
                    DisplayName = "Claude 3.5 Haiku",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                }
            ]
        },
        new()
        {
            Id = "google",
            DisplayName = "Google (Gemini)",
            OfficialWebsiteUrl = "https://gemini.google.com",
            Endpoint = "https://generativelanguage.googleapis.com/v1beta",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/google-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/google-color.svg",
            Schema = ModelProviderSchema.Google,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "gemini-3-pro-preview",
                    ModelId = "gemini-3-pro-preview",
                    DisplayName = "Gemini 3 Pro Preview",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gemini-3-flash-preview",
                    ModelId = "gemini-3-flash-preview",
                    DisplayName = "Gemini 3 Flash Preview",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gemini-2.5-pro",
                    ModelId = "gemini-2.5-pro",
                    DisplayName = "Gemini 2.5 Pro",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "gemini-2.5-flash",
                    ModelId = "gemini-2.5-flash",
                    DisplayName = "Gemini 2.5 Flash",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "gemini-2.5-flash-lite",
                    ModelId = "gemini-2.5-flash-lite",
                    DisplayName = "Gemini 2.5 Flash-Lite",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                }
            ]
        },
        new()
        {
            Id = "deepseek",
            DisplayName = "DeepSeek",
            Endpoint = "https://api.deepseek.com",
            OfficialWebsiteUrl = "https://www.deepseek.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/deepseek-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/deepseek-color.svg",
            Schema = ModelProviderSchema.DeepSeek,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "deepseek-chat",
                    ModelId = "deepseek-chat",
                    DisplayName = "DeepSeek V3.2 (Non-thinking Mode)",
                    MaxTokens = 128_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "deepseek-reasoner",
                    ModelId = "deepseek-reasoner",
                    DisplayName = "DeepSeek V3.2 (Thinking Mode)",
                    MaxTokens = 128_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                }
            ]
        },
        new()
        {
            Id = "moonshot",
            DisplayName = "Moonshot (Kimi)",
            Endpoint = "https://api.moonshot.cn/v1",
            OfficialWebsiteUrl = "https://www.moonshot.cn",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/moonshot-light.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "kimi-k2-0905-preview",
                    ModelId = "kimi-k2-0905-preview",
                    DisplayName = "Kimi K2",
                    MaxTokens = 262_144,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                },
                new ModelDefinitionTemplate
                {
                    Id = "kimi-k2-turbo-preview",
                    ModelId = "kimi-k2-turbo-preview",
                    DisplayName = "Kimi K2 Turbo",
                    MaxTokens = 262_144,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                },
                new ModelDefinitionTemplate
                {
                    Id = "kimi-k2-thinking",
                    ModelId = "kimi-k2-thinking",
                    DisplayName = "Kimi K2 Thinking",
                    MaxTokens = 262_144,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "kimi-k2-thinking-turbo",
                    ModelId = "kimi-k2-thinking-turbo",
                    DisplayName = "Kimi K2 Thinking Turbo",
                    MaxTokens = 262_144,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "kimi-latest",
                    ModelId = "kimi-latest",
                    DisplayName = "Kimi Latest",
                    MaxTokens = 128_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                    IsDefault = true
                }
            ]
        },
        new()
        {
            Id = "openrouter",
            DisplayName = "OpenRouter",
            OfficialWebsiteUrl = "https://openrouter.ai",
            Endpoint = "https://openrouter.ai/api/v1",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/openrouter-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/openrouter-light.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "google/gemini-2.5-flash",
                    ModelId = "google/gemini-2.5-flash",
                    DisplayName = "Google: Gemini 2.5 Flash",
                    MaxTokens = 1_048_576,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "anthropic/claude-sonnet-4.5",
                    ModelId = "anthropic/claude-sonnet-4.5",
                    DisplayName = "Anthropic: Claude Sonnet 4.5",
                    MaxTokens = 1_000_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                },
                new ModelDefinitionTemplate
                {
                    Id = "anthropic/claude-opus-4.5",
                    ModelId = "anthropic/claude-sonnet-4.5",
                    DisplayName = "Anthropic: Claude Opus 4.5",
                    MaxTokens = 200_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                },
                new ModelDefinitionTemplate
                {
                    Id = "deepseek/deepseek-v3.2",
                    ModelId = "deepseek/deepseek-v3.2",
                    DisplayName = "DeepSeek: DeepSeek V3.2",
                    MaxTokens = 163_840,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "openai/gpt-oss-120b",
                    ModelId = "openai/gpt-oss-120b",
                    DisplayName = "OpenAI: GPT-OSS 120B",
                    MaxTokens = 131_072,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "x-ai/grok-4-fast",
                    ModelId = "x-ai/grok-4-fast",
                    DisplayName = "X-AI: Grok 4 Fast",
                    MaxTokens = 2_000_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                    IsDefault = true
                }
            ]
        },
        new()
        {
            Id = "siliconcloud",
            DisplayName = "SiliconCloud (SiliconFlow)",
            OfficialWebsiteUrl = "https://www.siliconflow.cn",
            Endpoint = "https://api.siliconflow.cn/v1",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/siliconcloud-color.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "Qwen/Qwen3-8B",
                    ModelId = "Qwen/Qwen3-8B",
                    DisplayName = "Qwen3-8B (free)",
                    MaxTokens = 128_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "zai-org/GLM-4.6V",
                    ModelId = "zai-org/GLM-4.6V",
                    DisplayName = "GLM 4.6V",
                    MaxTokens = 128_000,
                    IsImageInputSupported = true,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "moonshotai/Kimi-K2-Thinking",
                    ModelId = "moonshotai/Kimi-K2-Thinking",
                    DisplayName = "Kimi K2 Thinking",
                    MaxTokens = 256_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "MiniMaxAI/MiniMax-M2",
                    ModelId = "MiniMaxAI/MiniMax-M2",
                    DisplayName = "MiniMax M2",
                    MaxTokens = 192_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "deepseek-ai/DeepSeek-V3.2",
                    ModelId = "deepseek-ai/DeepSeek-V3.2",
                    DisplayName = "DeepSeek-V3.2",
                    MaxTokens = 160_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                }
            ]
        },
        new()
        {
            Id = "ollama",
            DisplayName = "Ollama",
            OfficialWebsiteUrl = "https://ollama.com",
            Endpoint = "http://127.0.0.1:11434",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/ollama-dark.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/ollama-light.svg",
            Schema = ModelProviderSchema.Ollama,
            RequestTimeoutSeconds = 120, // Local models may take longer time.
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    Id = "gpt-oss:20b",
                    ModelId = "gpt-oss:20b",
                    DisplayName = "GPT-OSS 20B",
                    MaxTokens = 128_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = true,
                },
                new ModelDefinitionTemplate
                {
                    Id = "deepseek-r1:8b",
                    ModelId = "deepseek-r1:8b",
                    DisplayName = "DeepSeek R1 8B",
                    MaxTokens = 128_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = false,
                    IsDeepThinkingSupported = true,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    Id = "qwen3:8b",
                    ModelId = "qwen3:8b",
                    DisplayName = "Qwen 3 8B",
                    MaxTokens = 40_000,
                    IsImageInputSupported = false,
                    IsFunctionCallingSupported = true,
                    IsDeepThinkingSupported = false,
                }
            ]
        }
    ];

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
            .ModelDefinitions.FirstOrDefault(m => m.Id == ModelDefinitionTemplateId);
        set => ModelDefinitionTemplateId = value?.Id;
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
            owner.ModelId = modelDefinitionTemplate.Id;
            owner.IsImageInputSupported = modelDefinitionTemplate.IsImageInputSupported;
            owner.IsFunctionCallingSupported = modelDefinitionTemplate.IsFunctionCallingSupported;
            owner.IsDeepThinkingSupported = modelDefinitionTemplate.IsDeepThinkingSupported;
            owner.MaxTokens = modelDefinitionTemplate.MaxTokens;
        }
        else
        {
            owner.ModelId = string.Empty;
            owner.IsImageInputSupported = false;
            owner.IsFunctionCallingSupported = false;
            owner.IsDeepThinkingSupported = false;
            owner.MaxTokens = 81920;
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
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Endpoint_Header,
        LocaleKey.CustomAssistant_Endpoint_Description)]
    [CustomValidation(typeof(AdvancedModelProviderConfigurator), nameof(ValidateEndpoint))]
    public string? Endpoint
    {
        get => owner.Endpoint;
        set => owner.Endpoint = value;
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
        });

    [DynamicResourceKey(
        LocaleKey.CustomAssistant_Schema_Header,
        LocaleKey.CustomAssistant_Schema_Description)]
    public ModelProviderSchema Schema
    {
        get => owner.Schema;
        set => owner.Schema = value;
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

    /// <summary>
    /// Indicates whether the model supports image input capabilities.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsImageInputSupported_Header,
        LocaleKey.CustomAssistant_IsImageInputSupported_Description)]
    public bool IsImageInputSupported
    {
        get => owner.IsImageInputSupported;
        set => owner.IsImageInputSupported = value;
    }

    /// <summary>
    /// Indicates whether the model supports function calling capabilities.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsFunctionCallingSupported_Header,
        LocaleKey.CustomAssistant_IsFunctionCallingSupported_Description)]
    public bool IsFunctionCallingSupported
    {
        get => owner.IsFunctionCallingSupported;
        set => owner.IsFunctionCallingSupported = value;
    }

    /// <summary>
    /// Indicates whether the model supports tool calls.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_IsDeepThinkingSupported_Header,
        LocaleKey.CustomAssistant_IsDeepThinkingSupported_Description)]
    public bool IsDeepThinkingSupported
    {
        get => owner.IsDeepThinkingSupported;
        set => owner.IsDeepThinkingSupported = value;
    }

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// aka, the maximum context length.
    /// </summary>
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_MaxTokens_Header,
        LocaleKey.CustomAssistant_MaxTokens_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int MaxTokens
    {
        get => owner.MaxTokens;
        set => owner.MaxTokens = value;
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
        Backup(IsImageInputSupported);
        Backup(IsFunctionCallingSupported);
        Backup(IsDeepThinkingSupported);
        Backup(MaxTokens);
    }

    public void Apply()
    {
        Endpoint = Restore(Endpoint);
        Schema = Restore(Schema);
        ModelId = Restore(ModelId);
        IsImageInputSupported = Restore(IsImageInputSupported);
        IsFunctionCallingSupported = Restore(IsFunctionCallingSupported);
        IsDeepThinkingSupported = Restore(IsDeepThinkingSupported);
        MaxTokens = Restore(MaxTokens);
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