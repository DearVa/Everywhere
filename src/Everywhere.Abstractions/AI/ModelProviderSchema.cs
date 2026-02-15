using MessagePack;

namespace Everywhere.AI;

/// <summary>
/// Provides schema definitions and constants for model providers.
/// </summary>
public enum ModelProviderSchema
{
    /// <summary>
    /// Official provider schema.
    /// </summary>
    [IgnoreMember]
    Official = -1,

    OpenAI,
    OpenAIResponses,
    Anthropic,
    Google,
    Ollama,
    DeepSeek
}