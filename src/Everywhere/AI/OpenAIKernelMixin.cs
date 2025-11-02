using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;
using OpenAI.Chat;
using Everywhere.Configuration;
using BinaryContent = System.ClientModel.BinaryContent;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for OpenAI models.
/// </summary>
public sealed class OpenAIKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public OpenAIKernelMixin(CustomAssistant customAssistant) : base(customAssistant)
    {
        var httpClient = ProxyHttpClientFactory.CreateHttpClient();
        var optimizedClient = new OptimizedChatClient(
            ModelId,
            new ApiKeyCredential(ApiKey.IsNullOrWhiteSpace() ? "NO_API_KEY" : ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(Endpoint, UriKind.Absolute)
            },
            httpClient);

        ChatCompletionService = new OptimizedOpenAIApiClient(
            optimizedClient.AsIChatClient(),
            this,
            httpClient
        ).AsChatCompletionService();
    }

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null)
    {
        double? temperature = _customAssistant.Temperature.IsCustomValueSet ? _customAssistant.Temperature.ActualValue : null;
        double? topP = _customAssistant.TopP.IsCustomValueSet ? _customAssistant.TopP.ActualValue : null;
        double? presencePenalty = _customAssistant.PresencePenalty.IsCustomValueSet ? _customAssistant.PresencePenalty.ActualValue : null;
        double? frequencyPenalty = _customAssistant.FrequencyPenalty.IsCustomValueSet ? _customAssistant.FrequencyPenalty.ActualValue : null;

        return new OpenAIPromptExecutionSettings
        {
            Temperature = temperature,
            TopP = topP,
            PresencePenalty = presencePenalty,
            FrequencyPenalty = frequencyPenalty,
            FunctionChoiceBehavior = functionChoiceBehavior
        };
    }

    /// <summary>
    /// optimized wrapper around OpenAI's ChatClient to set custom User-Agent header.
    /// </summary>
    /// <remarks>
    /// Layered upon layer, are you an onion?
    /// </remarks>
    private sealed class OptimizedChatClient(string modelId, ApiKeyCredential credential, OpenAIClientOptions options, HttpClient httpClient)
        : ChatClient(modelId, credential, ConfigureOptions(options, httpClient))
    {
        public override Task<ClientResult> CompleteChatAsync(BinaryContent content, RequestOptions? options = null)
        {
            options?.SetHeader(
                "UserAgent",
                $"Everywhere/{typeof(OpenAIKernelMixin).Assembly.GetName().Version?.ToString() ?? "1.0.0"} " +
#if IsWindows
                "(Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#elif IsOSX
                "(Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#else
                "(X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#endif
            );

            return base.CompleteChatAsync(content, options);
        }

        private static OpenAIClientOptions ConfigureOptions(OpenAIClientOptions options, HttpClient httpClient)
        {
            options ??= new OpenAIClientOptions();
            options.Transport = new HttpClientPipelineTransport(httpClient);
            return options;
        }
    }

    /// <summary>
    /// optimized wrapper around OpenAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient(
        IChatClient client,
        OpenAIKernelMixin owner,
        HttpClient transportHttpClient) : IChatClient
    {
        private static readonly PropertyInfo? ChoicesProperty = typeof(StreamingChatCompletionUpdate).GetProperty("Choices", BindingFlags.NonPublic | BindingFlags.Instance);
        private static PropertyInfo? _choiceCountProperty;
        private static PropertyInfo? _choiceIndexerProperty;
        private static PropertyInfo? _choiceDeltaProperty;
        private static PropertyInfo? _deltaRawDataProperty;


        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            messages = EnsureCompatibilityFields(messages);
            return client.GetResponseAsync(messages, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            messages = EnsureCompatibilityFields(messages);

            // cache the value to avoid property changes during enumeration
            var isDeepThinkingSupported = owner.IsDeepThinkingSupported;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Why you keep reasoning in the fucking internal properties, OpenAI???
                if (isDeepThinkingSupported && update is { Text: not { Length: > 0 }, RawRepresentation: StreamingChatCompletionUpdate detail })
                {
                    // Get the value of the internal 'Choices' property.
                    var choices = ChoicesProperty?.GetValue(detail);
                    if (choices is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the 'Count' property of the Choices collection.
                    _choiceCountProperty ??= choices.GetType().GetProperty("Count");
                    if (_choiceCountProperty?.GetValue(choices) is not int count || count == 0)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the indexer 'Item' property of the Choices collection.
                    _choiceIndexerProperty ??= choices.GetType().GetProperty("Item");
                    if (_choiceIndexerProperty is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Get the first choice from the collection.
                    var firstChoice = _choiceIndexerProperty.GetValue(choices, [0]);
                    if (firstChoice is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the 'Delta' property of a choice.
                    _choiceDeltaProperty ??= firstChoice.GetType().GetProperty("Delta", BindingFlags.Instance | BindingFlags.NonPublic);
                    var delta = _choiceDeltaProperty?.GetValue(firstChoice);
                    if (delta is null)
                    {
                        yield return update;
                        continue;
                    }

                    // Cache PropertyInfo for the internal 'SerializedAdditionalRawData' property of the delta.
                    _deltaRawDataProperty ??= delta.GetType().GetProperty(
                        "SerializedAdditionalRawData",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    // Extract and process the raw data if it exists.
                    if (_deltaRawDataProperty?.GetValue(delta) is not IDictionary<string, BinaryData?> source ||
                        !source.TryGetValue("reasoning_content", out var reasoningContentBinaryData) ||
                        reasoningContentBinaryData is null ||
                        reasoningContentBinaryData.Length <= 2 || // start and end are quotes
                        JsonSerializer.Deserialize<string>(reasoningContentBinaryData.ToString()) is not { Length: > 0 } reasoningContent)
                    {
                        yield return update;
                        continue;
                    }

                    update.Contents.Add(
                        new TextContent(reasoningContent)
                        {
                            AdditionalProperties = ReasoningProperties
                        });
                    update.AdditionalProperties = ApplyReasoningProperties(update.AdditionalProperties);
                }

                // Ensure that all FunctionCallContent items have a unique CallId.
                for (var i = 0; i < update.Contents.Count; i++)
                {
                    var item = update.Contents[i];
                    if (item is FunctionCallContent { Name.Length: > 0, CallId: null or { Length: 0 } } missingIdContent)
                    {
                        // Generate a unique ToolCallId for the function call update.
                        update.Contents[i] = new FunctionCallContent(
                            Guid.CreateVersion7().ToString("N"),
                            missingIdContent.Name,
                            missingIdContent.Arguments);
                    }
                }

                yield return update;
            }
        }

        /// <summary>
        /// Ensure each ChatMessage contains the compatibility fields required by some models/clients.
        /// We use reflection to avoid compile-time dependency on the concrete ChatMessage shape.
        /// The fields added are: 'refusal', 'annotations', 'audio', 'function_call' (all set to null).
        /// </summary>
        private static IEnumerable<ChatMessage> EnsureCompatibilityFields(IEnumerable<ChatMessage> messages)
        {
            foreach (var msg in messages)
            {
                if (msg.AdditionalProperties is { } dict)
                {
                    dict.TryAdd("refusal", null);
                    dict.TryAdd("annotations", null);
                    dict.TryAdd("audio", null);
                    dict.TryAdd("function_call", null);
                }
                else
                {
                    msg.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["refusal"] = null,
                        ["annotations"] = null,
                        ["audio"] = null,
                        ["function_call"] = null
                    };
                }

                yield return msg;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return client.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            client.Dispose();
            transportHttpClient.Dispose();
        }
    }
}