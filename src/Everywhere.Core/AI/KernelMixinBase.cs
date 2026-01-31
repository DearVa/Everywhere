using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract class KernelMixinBase(CustomAssistant customAssistant) : IKernelMixin
{
    // cache properties for comparison
    public ModelProviderSchema Schema { get; } = customAssistant.Schema;

    public string Endpoint { get; } = customAssistant.Endpoint?.Trim().Trim('/') ??
        throw new HandledChatException(
            new InvalidOperationException("Endpoint cannot be empty."),
            HandledChatExceptionType.InvalidEndpoint);

    public string? ApiKey { get; } = Configuration.ApiKey.GetKey(customAssistant.ApiKey);

    protected string EnsureApiKey() => ApiKey ??
        throw new HandledChatException(
            new InvalidOperationException("API Key cannot be empty."),
            HandledChatExceptionType.InvalidApiKey);

    public string ModelId { get; } = customAssistant.ModelId ??
        throw new HandledChatException(
            new InvalidOperationException("Model ID cannot be empty."),
            HandledChatExceptionType.InvalidConfiguration);

    public int RequestTimeoutSeconds { get; } = customAssistant.RequestTimeoutSeconds;

    public int ContextWindow => _customAssistant.MaxTokens;

    public bool IsImageInputSupported => _customAssistant.IsImageInputSupported;

    public bool IsFunctionCallingSupported => _customAssistant.IsFunctionCallingSupported;

    public bool IsDeepThinkingSupported => _customAssistant.IsDeepThinkingSupported;

    public abstract IChatCompletionService ChatCompletionService { get; }

    /// <summary>
    /// WARNING: properties are mutable!
    /// </summary>
    protected readonly CustomAssistant _customAssistant = customAssistant;

    /// <summary>
    /// indicates whether the model is reasoning
    /// </summary>
    protected static readonly AdditionalPropertiesDictionary ReasoningProperties = new()
    {
        ["reasoning"] = true
    };

    /// <summary>
    /// Applies reasoning properties to the given dictionary. Indicating current chat message contains reasoning content.
    /// </summary>
    /// <param name="dictionary"></param>
    /// <returns></returns>
    protected static AdditionalPropertiesDictionary ApplyReasoningProperties(AdditionalPropertiesDictionary? dictionary)
    {
        if (dictionary is null) return ReasoningProperties;
        dictionary["reasoning"] = true;
        return dictionary;
    }

    /// <summary>
    /// Sets a customizable property's actual value into the extension data of the prompt execution settings if it has a custom value set.
    /// </summary>
    /// <param name="settings"></param>
    /// <param name="customizable"></param>
    /// <param name="propertyName"></param>
    /// <typeparam name="T"></typeparam>
    protected static void SetPromptExecutionSettingsExtensionData<T>(
        PromptExecutionSettings settings,
        Customizable<T> customizable,
        string propertyName) where T : struct
    {
        if (!customizable.IsCustomValueSet) return;

        settings.ExtensionData ??= new Dictionary<string, object>();
        settings.ExtensionData[propertyName] = customizable.ActualValue;
    }

    public virtual bool IsPersistentMessageMetadataKey(string key) => false;

    public virtual bool IsPersistentSpanMetadataKey(string key) => false;

    /// <summary>
    /// Default implementation includes temperature and top_p from the custom assistant.
    /// </summary>
    /// <param name="functionChoiceBehavior"></param>
    /// <param name="reasoningEffortLevel"></param>
    /// <returns></returns>
    public virtual PromptExecutionSettings GetPromptExecutionSettings(
        FunctionChoiceBehavior? functionChoiceBehavior = null,
        ReasoningEffortLevel reasoningEffortLevel = ReasoningEffortLevel.Default)
    {
        var result = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = functionChoiceBehavior,
            ExtensionData = new Dictionary<string, object>(1)
            {
                { "reasoning_effort_level", reasoningEffortLevel }
            }
        };

        SetPromptExecutionSettingsExtensionData(result, _customAssistant.Temperature, "temperature");
        SetPromptExecutionSettingsExtensionData(result, _customAssistant.TopP, "top_p");

        return result;
    }

    public Task CheckConnectivityAsync(CancellationToken cancellationToken = default) => ChatCompletionService.GetChatMessageContentAsync(
        [
            new ChatMessageContent(AuthorRole.System, "You're a helpful assistant."),
            new ChatMessageContent(AuthorRole.User, Prompts.TestPrompt)
        ],
        GetPromptExecutionSettings(),
        cancellationToken: cancellationToken);

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}