namespace Everywhere.AI;

/// <summary>
/// Provides schema definitions and constants for model providers.
/// </summary>
public enum ModelProviderSchema
{
    [DynamicResourceKey(LocaleKey.ModelProviderSchema_OpenAI)]
    OpenAI,
    [DynamicResourceKey(LocaleKey.ModelProviderSchema_Anthropic)]
    Anthropic,
    [DynamicResourceKey(LocaleKey.ModelProviderSchema_Google)]
    Google,
    [DynamicResourceKey(LocaleKey.ModelProviderSchema_Ollama)]
    Ollama
}