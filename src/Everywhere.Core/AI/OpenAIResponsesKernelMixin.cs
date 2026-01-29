using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Responses;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for OpenAI models via Responses API.
/// </summary>
public sealed class OpenAIResponsesKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public OpenAIResponsesKernelMixin(
        CustomAssistant customAssistant,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant)
    {
        ChatCompletionService = new OptimizedOpenAIApiClient(
            new ResponsesClient(
                ModelId,
                new ApiKeyCredential(ApiKey.IsNullOrWhiteSpace() ? "NO_API_KEY" : ApiKey),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    Transport = new HttpClientPipelineTransport(httpClient, true, loggerFactory)
                }
            ).AsIChatClient(),
            this
        ).AsChatCompletionService();
    }

    /// <summary>
    /// optimized wrapper around OpenAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient(IChatClient client, OpenAIResponsesKernelMixin owner) : DelegatingChatClient(client)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // MEAI not supporting Deep Thinking will skip adding the reasoning options
            // This is a workaround
            options ??= new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            // cache the value to avoid property changes during enumeration
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Ensure that all FunctionCallContent items have a unique CallId.
                for (var i = 0; i < update.Contents.Count; i++)
                {
                    var content = update.Contents[i];
                    switch (content)
                    {
                        case FunctionCallContent { Name.Length: > 0, CallId: null or { Length: 0 } } missingIdContent:
                        { 
                            // Generate a unique ToolCallId for the function call update.
                            update.Contents[i] = new FunctionCallContent(
                                Guid.CreateVersion7().ToString("N"),
                                missingIdContent.Name,
                                missingIdContent.Arguments);
                            break;
                        }
                        case TextReasoningContent reasoningContent:
                        { 
                            // Semantic Kernel won't handle TextReasoningContent, convert it to TextContent with reasoning properties
                            update.Contents[i] = new TextContent(reasoningContent.Text)
                            {
                                AdditionalProperties = ReasoningProperties
                            };
                            update.AdditionalProperties = ApplyReasoningProperties(update.AdditionalProperties);
                            break;
                        }
                    }
                }

                yield return update;
            }
        }

        private object? RawRepresentationFactory(IChatClient chatClient) => owner.IsDeepThinkingSupported ?
            new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Detailed
                }
            } :
            null;
    }
}