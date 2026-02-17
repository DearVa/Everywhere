using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

/// <summary>
/// Interface for mixing Semantic Kernel and Kernel Memory services.
/// </summary>
public interface IKernelMixin : IDisposable
{
    /// <summary>
    /// Gets the chat completion service.
    /// </summary>
    IChatCompletionService ChatCompletionService { get; }

    bool SupportsReasoning { get; }

    bool SupportsToolCall { get; }

    /// <summary>
    /// Gets the maximum number of tokens allowed in a single request to the model.
    /// </summary>
    int ContextLimit { get; }

    /// <summary>
    /// Determines if the specified message-level metadata key is needed to be persisted.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool IsPersistentMessageMetadataKey(string key);

    /// <summary>
    /// Determines if the specified span-level metadata key is needed to be persisted. e.g. reasoningSteps for DeepSeek.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool IsPersistentSpanMetadataKey(string key);

    /// <summary>
    /// Gets the prompt execution settings.
    /// </summary>
    /// <param name="functionChoiceBehavior"></param>
    /// <param name="reasoningEffortLevel"></param>
    /// <returns>The prompt execution settings.</returns>
    PromptExecutionSettings? GetPromptExecutionSettings(
        FunctionChoiceBehavior? functionChoiceBehavior = null,
        ReasoningEffortLevel? reasoningEffortLevel = null);

    /// <summary>
    /// Checks the connectivity to the AI service. Throws an exception if the connectivity check fails.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CheckConnectivityAsync(CancellationToken cancellationToken = default);
}