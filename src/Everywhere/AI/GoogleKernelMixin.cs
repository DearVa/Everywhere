using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Google Gemini models.
/// </summary>
public sealed class GoogleKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public GoogleKernelMixin(
        CustomAssistant customAssistant,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant)
    {
        httpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);

        var service = new GoogleAIGeminiChatCompletionService(
            ModelId,
            ApiKey ?? string.Empty,
            httpClient: httpClient,
            loggerFactory: loggerFactory);

        ApplyCustomEndpoint(service);

        ChatCompletionService = new OptimizedGeminiChatCompletionService(service);
    }

    private void ApplyCustomEndpoint(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields)]
        GoogleAIGeminiChatCompletionService service)
    {
        // We need to modify the endpoint by reflection because the GoogleAIGeminiChatCompletionService does not expose it.

        var client = typeof(GoogleAIGeminiChatCompletionService)
            .GetField("_chatCompletionClient", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(service);
        if (client is null) return;

        // this._chatGenerationEndpoint = new Uri($"https://generativelanguage.googleapis.com/{apiVersionSubLink}/models/{this._modelId}:generateContent");
        // this._chatStreamingEndpoint = new Uri($"https://generativelanguage.googleapis.com/{apiVersionSubLink}/models/{this._modelId}:streamGenerateContent?alt=sse");
        client.GetType()
            .GetField("_chatGenerationEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)?
            .SetValue(client, new Uri($"{Endpoint}/models/{ModelId}:generateContent"));

        client.GetType()
            .GetField("_chatStreamingEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)?
            .SetValue(client, new Uri($"{Endpoint}/models/{ModelId}:streamGenerateContent?alt=sse"));
    }

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null)
    {
        double? temperature = _customAssistant.Temperature.IsCustomValueSet ? _customAssistant.Temperature.ActualValue : null;
        double? topP = _customAssistant.TopP.IsCustomValueSet ? _customAssistant.TopP.ActualValue : null;
        int? maxTokens = _customAssistant.MaxTokens.IsCustomValueSet ? _customAssistant.MaxTokens.ActualValue : null;

        // Convert FunctionChoiceBehavior to GeminiToolCallBehavior
        GeminiToolCallBehavior? toolCallBehavior = null;
        if (functionChoiceBehavior is not null)
        {
            // Check if it's auto-invoke based on behavior type
            var behaviorType = functionChoiceBehavior.GetType();
            bool autoInvoke = false;
            
            try
            {
                var autoInvokeField = behaviorType.GetField("AutoInvoke", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (autoInvokeField is not null && autoInvokeField.FieldType == typeof(bool))
                {
                    autoInvoke = (bool)autoInvokeField.GetValue(functionChoiceBehavior)!;
                }
            }
            catch
            {
                // Ignore
            }

            // Check if it's NoneFunctionChoiceBehavior
            if (behaviorType.Name != nameof(NoneFunctionChoiceBehavior))
            {
                toolCallBehavior = autoInvoke
                    ? GeminiToolCallBehavior.AutoInvokeKernelFunctions
                    : GeminiToolCallBehavior.EnableKernelFunctions;
            }
        }

        return new GeminiPromptExecutionSettings
        {
            Temperature = temperature,
            TopP = topP,
            MaxTokens = maxTokens,
            ToolCallBehavior = toolCallBehavior,
            ThinkingConfig = IsDeepThinkingSupported ? new GeminiThinkingConfig
            {
                ThinkingBudget = -1,
                IncludeThoughts = true
            } : null
        };
    }

    /// <summary>
    /// Wrapper around Google Gemini's IChatCompletionService to inject Usage metadata.
    /// The underlying semantic-kernel Gemini connector now supports FunctionCallContent/FunctionResultContent natively.
    /// </summary>
    private sealed class OptimizedGeminiChatCompletionService(IChatCompletionService innerService) : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes => innerService.Attributes;

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            return innerService.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var content in innerService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               executionSettings,
                               kernel,
                               cancellationToken))
            {
                // inject GeminiMetadata into "Usage" key for consistent handling in ChatService
                if (content.Metadata is GeminiMetadata geminiMetadata)
                {
                    var usageDetails = new UsageDetails
                    {
                        InputTokenCount = geminiMetadata.PromptTokenCount,
                        OutputTokenCount = geminiMetadata.CandidatesTokenCount + geminiMetadata.ThoughtsTokenCount,
                        TotalTokenCount = geminiMetadata.TotalTokenCount
                    };

                    var newMetadata = new Dictionary<string, object?>();
                    if (content.Metadata is not null)
                    {
                        foreach (var (key, value) in content.Metadata)
                        {
                            newMetadata[key] = value;
                        }
                    }
                    newMetadata["Usage"] = usageDetails;

                    yield return new StreamingChatMessageContent(
                        content.Role,
                        content.Content,
                        content.InnerContent,
                        content.ChoiceIndex,
                        content.ModelId,
                        content.Encoding,
                        newMetadata)
                    {
                        Items = content.Items
                    };
                }
                else
                {
                    yield return content;
                }
            }
        }
    }
}