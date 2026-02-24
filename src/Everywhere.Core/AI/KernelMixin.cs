using Everywhere.Common;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract class KernelMixin(CustomAssistant customAssistant, ModelConnection connection) : IModelDefinition, IDisposable
{
    /// <summary>
    /// The resolved connection parameters for cache comparison and SDK initialization.
    /// </summary>
    public ModelConnection Connection { get; } = connection;

    /// <summary>
    /// Convenience accessor for the resolved endpoint (already normalized, never null).
    /// </summary>
    protected string Endpoint => Connection.Endpoint;

    /// <summary>
    /// Convenience accessor for the resolved API key (null means no key needed / handled by HttpClient).
    /// </summary>
    protected string? ApiKey => Connection.ApiKey;

    public string ModelId { get; } = customAssistant.ModelId ??
        throw new HandledChatException(
            new InvalidOperationException("Model ID cannot be empty."),
            HandledChatExceptionType.InvalidConfiguration);

    public string? Name { get; } = customAssistant.Name;

    public int RequestTimeoutSeconds { get; } = customAssistant.RequestTimeoutSeconds;

    public bool SupportsReasoning => customAssistant.SupportsReasoning;

    public bool SupportsToolCall => customAssistant.SupportsToolCall;

    public Modalities InputModalities => customAssistant.InputModalities;

    public Modalities OutputModalities => customAssistant.OutputModalities;

    public int ContextLimit => customAssistant.ContextLimit;

    public int OutputLimit => customAssistant.OutputLimit;

    protected double? Temperature => customAssistant.Temperature.IsCustomValueSet ? customAssistant.Temperature.ActualValue : null;

    protected double? TopP => customAssistant.TopP.IsCustomValueSet ? customAssistant.TopP.ActualValue : null;

    public abstract IChatCompletionService ChatCompletionService { get; }

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
        ReasoningEffortLevel? reasoningEffortLevel = null)
    {
        var result = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = functionChoiceBehavior
        };

        if (reasoningEffortLevel.HasValue)
        {
            result.ExtensionData = new Dictionary<string, object>(1)
            {
                { "reasoning_effort_level", reasoningEffortLevel }
            };
        }

        SetPromptExecutionSettingsExtensionData(result, Temperature, "temperature");
        SetPromptExecutionSettingsExtensionData(result, TopP, "top_p");

        return result;

        static void SetPromptExecutionSettingsExtensionData(PromptExecutionSettings settings, double? value, string propertyName)
        {
            if (!value.HasValue) return;

            settings.ExtensionData ??= new Dictionary<string, object>();
            settings.ExtensionData[propertyName] = value.Value;
        }
    }

    public async Task CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var innerCancellationTokenSource = new CancellationTokenSource();
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            innerCancellationTokenSource.Token);

        await foreach (var _ in ChatCompletionService.GetStreamingChatMessageContentsAsync(
                           [
                               new ChatMessageContent(AuthorRole.System, "You're a helpful assistant."),
                               new ChatMessageContent(AuthorRole.User, Prompts.TestPrompt)
                           ],
                           GetPromptExecutionSettings(),
                           cancellationToken: linkedCancellationTokenSource.Token))
        {
            // if we can get any response without exception, we consider the connectivity check passed, then we can cancel the request to avoid unnecessary cost.
            await innerCancellationTokenSource.CancelAsync();
            return;
        }
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}