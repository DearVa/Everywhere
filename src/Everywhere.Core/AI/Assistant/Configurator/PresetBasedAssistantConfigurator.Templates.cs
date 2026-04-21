using System.Text.Json.Serialization;
using Everywhere.Configuration;

namespace Everywhere.AI.Configurator;

partial class PresetBasedAssistantConfigurator
{
    /// <summary>
    /// Helper property to get all supported model provider templates.
    /// </summary>
    [JsonIgnore]
    [HiddenSettingsItem]
    public static ModelProviderTemplate[] ModelProviderTemplates { get; } =
    [
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
                    ModelId = "gpt-5.4",
                    Name = "GPT-5.4",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_050_000,
                    OutputLimit = 128_000,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.4-mini",
                    Name = "GPT-5.4 mini",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.4-nano",
                    Name = "GPT-5.4 nano",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.2",
                    Name = "GPT-5.2",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5.1",
                    Name = "GPT-5.1",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5",
                    Name = "GPT-5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5-mini",
                    Name = "GPT-5 mini",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-5-nano",
                    Name = "GPT-5 nano",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 400_000,
                    // InputLimit = 272_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "o4-mini",
                    Name = "o4-mini",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 100_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4.1",
                    Name = "GPT 4.1",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_047_576,
                    OutputLimit = 32_768,
                    Specializations = ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4.1-mini",
                    Name = "GPT 4.1 mini",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_047_576,
                    OutputLimit = 32_768
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gpt-4o",
                    Name = "GPT-4o",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128_000,
                    OutputLimit = 16_384
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
                    ModelId = "claude-opus-4-7",
                    Name = "Claude Opus 4.7",
                    SupportsReasoning = true, // Adaptive Only, spacial case
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-6",
                    Name = "Claude Opus 4.6",
                    SupportsReasoning = true, // Adaptive Only, spacial case
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-5",
                    Name = "Claude Opus 4.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-6",
                    Name = "Claude Sonnet 4.6",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 64_000,
                    IsDefault = true,
                    Specializations = ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-5",
                    Name = "Claude Sonnet 4.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-haiku-4-5",
                    Name = "Claude Haiku 4.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-1",
                    Name = "Claude Opus 4.1",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 32_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-opus-4-0",
                    Name = "Claude Opus 4",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    DeprecationDate = new DateOnly(2026, 6, 15),
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 32_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "claude-sonnet-4-0",
                    Name = "Claude Sonnet 4",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    DeprecationDate = new DateOnly(2026, 6, 15),
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 64_000
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
                    ModelId = "gemini-3.1-pro-preview",
                    Name = "Gemini 3.1 Pro Preview",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3-flash-preview",
                    Name = "Gemini 3 Flash Preview",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                    IsDefault = true,
                    Specializations = ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-pro",
                    Name = "Gemini 2.5 Pro",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-flash",
                    Name = "Gemini 2.5 Flash",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-3.1-flash-lite-preview",
                    Name = "Gemini 3.1 Flash-Lite",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "gemini-2.5-flash-lite",
                    Name = "Gemini 2.5 Flash-Lite",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536
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
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-chat",
                    Name = "DeepSeek V3.2 (Non-thinking Mode)",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_072,
                    OutputLimit = 8_192
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-reasoner",
                    Name = "DeepSeek V3.2 (Thinking Mode)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128_000,
                    OutputLimit = 64_000,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                }
            ]
        },
        new()
        {
            Id = "qiniu",
            DisplayName = "Qiniu",
            Endpoint = "https://api.qnaigc.com/v1",
            OfficialWebsiteUrl = "https://www.qiniu.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/qiniu-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/qiniu-color.svg",
            Schema = ModelProviderSchema.OpenAI,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-v3",
                    Name = "DeepSeek V3",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 128_000,
                    OutputLimit = 8_192,
                    IsDefault = true
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
                    ModelId = "kimi-k2.5",
                    Name = "Kimi K2.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Video,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2-0905-preview",
                    Name = "Kimi K2",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2-turbo-preview",
                    Name = "Kimi K2 Turbo",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2-thinking",
                    Name = "Kimi K2 Thinking",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "kimi-k2-thinking-turbo",
                    Name = "Kimi K2 Thinking Turbo",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_144,
                    OutputLimit = 262_144,
                }
            ]
        },
        new()
        {
            Id = "minimax",
            DisplayName = "MiniMax",
            Endpoint = "https://api.minimaxi.com/anthropic",
            OfficialWebsiteUrl = "https://minimaxi.com",
            DarkIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            LightIconUrl = "avares://Everywhere.Core/Assets/Icons/minimax-color.svg",
            Schema = ModelProviderSchema.Anthropic,
            ModelDefinitions =
            [
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.7",
                    Name = "MiniMax-M2.7",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.7-highspeed",
                    Name = "MiniMax-M2.7-highspeed",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.5",
                    Name = "MiniMax-M2.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.5-highspeed",
                    Name = "MiniMax-M2.5-highspeed",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.1",
                    Name = "MiniMax-M2.1",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2.1-highspeed",
                    Name = "MiniMax-M2.1-highspeed",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMax-M2",
                    Name = "MiniMax-M2",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 196_608,
                    OutputLimit = 128_000
                },
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
                // According to 2026/03/31 Rankings
                new ModelDefinitionTemplate
                {
                    ModelId = "xiaomi/mimo-v2-pro",
                    Name = "Xiaomi: MiMo-V2-Pro",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "stepfun/step-3.5-flash:free",
                    Name = "StepFun: Step 3.5 Flash (free)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 256_000,
                    OutputLimit = 256_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "minimax/minimax-m2.7",
                    Name = "MiniMax: MiniMax M2.7",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek/deepseek-v3.2",
                    Name = "DeepSeek: DeepSeek V3.2",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 163_840,
                    OutputLimit = 65_536,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-sonnet-4.6",
                    Name = "Anthropic: Claude Sonnet 4.6",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-opus-4.6",
                    Name = "Anthropic: Claude Opus 4.6",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 128_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "google/gemini-3-flash-preview",
                    Name = "Google: Gemini 3 Flash Preview",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "z-ai/glm-5-turbo",
                    Name = "Z.ai: GLM 5 Turbo",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 202_752,
                    OutputLimit = 131_072,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "minimax/minimax-m2.5",
                    Name = "MiniMax: MiniMax M2.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 204_800,
                    OutputLimit = 131_072,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "x-ai/grok-4.1-fast",
                    Name = "X-AI: Grok 4.1 Fast",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 2_000_000,
                    OutputLimit = 30_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "google/gemini-2.5-flash",
                    Name = "Google: Gemini 2.5 Flash",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Audio | Modalities.Video | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_048_576,
                    OutputLimit = 65_536,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-opus-4.5",
                    Name = "Anthropic: Claude Opus 4.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 200_000,
                    OutputLimit = 32_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "anthropic/claude-sonnet-4.5",
                    Name = "Anthropic: Claude Sonnet 4.5",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image | Modalities.Pdf,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 1_000_000,
                    OutputLimit = 64_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "openai/gpt-oss-120b",
                    Name = "OpenAI: GPT-OSS 120B",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_072,
                    OutputLimit = 32_768,
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
                // According to 2026/03/31 Models
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/MiniMaxAI/MiniMax-M2.5",
                    Name = "MiniMax-M2.5 (Pro)",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 192_000,
                    OutputLimit = 131_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/zai-org/GLM-5",
                    Name = "GLM-5 (Pro)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 205_000,
                    OutputLimit = 205_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/moonshotai/Kimi-K2.5",
                    Name = "Kimi-K2.5 (Pro)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_000,
                    OutputLimit = 262_000,
                    Specializations = ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/zai-org/GLM-4.7",
                    Name = "GLM-4.7 (Pro)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 205_000,
                    OutputLimit = 205_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-ai/DeepSeek-V3.2",
                    Name = "DeepSeek-V3.2",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 164_000,
                    OutputLimit = 164_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Pro/deepseek-ai/DeepSeek-V3.2",
                    Name = "DeepSeek-V3.2 (Pro)",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 164_000,
                    OutputLimit = 164_000
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "Qwen/Qwen3-8B",
                    Name = "Qwen3-8B (free)",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_000,
                    OutputLimit = 131_000,
                    IsDefault = true,
                    Specializations = ModelSpecializations.TitleGeneration
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "zai-org/GLM-4.6V",
                    Name = "GLM 4.6V",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text | Modalities.Image,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 131_000,
                    OutputLimit = 131_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "moonshotai/Kimi-K2-Thinking",
                    Name = "Kimi K2 Thinking",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 262_000,
                    OutputLimit = 262_000,
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "MiniMaxAI/MiniMax-M2.1",
                    Name = "MiniMax M2.1",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 197_000,
                    OutputLimit = 131_000,
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
                    ModelId = "gpt-oss:20b",
                    Name = "GPT-OSS 20B",
                    SupportsReasoning = true,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 64_000,
                    OutputLimit = 4_096,
                    Specializations = ModelSpecializations.TitleGeneration | ModelSpecializations.ContextCompression
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "deepseek-r1:8b",
                    Name = "DeepSeek R1 8B",
                    SupportsReasoning = true,
                    SupportsToolCall = false,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 64_000,
                    OutputLimit = 4_096,
                    IsDefault = true
                },
                new ModelDefinitionTemplate
                {
                    ModelId = "qwen3:8b",
                    Name = "Qwen 3 8B",
                    SupportsReasoning = false,
                    SupportsToolCall = true,
                    InputModalities = Modalities.Text,
                    OutputModalities = Modalities.Text,
                    ContextLimit = 64_000,
                    OutputLimit = 4_096,
                }
            ]
        }
    ];
}