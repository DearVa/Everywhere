using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Chat;
using ZLinq;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for OpenAI models via Chat Completions.
/// </summary>
public class OpenAIKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public OpenAIKernelMixin(
        CustomAssistant customAssistant,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant)
    {
        ChatCompletionService = new OptimizedOpenAIApiClient(
            new ChatClient(
                ModelId,
                // some models don't need API key (e.g. LM Studio)
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
    /// Hook called before sending a streaming chat request.
    /// </summary>
    protected virtual Task BeforeStreamingRequestAsync(IList<ChatMessage> messages, ref ChatOptions? options)
    {
        // If deep thinking is not supported, skip processing.
        if (!_customAssistant.IsDeepThinkingSupported) return Task.CompletedTask;

        options ??= new ChatOptions();
        options.RawRepresentationFactory = _ => new ChatCompletionOptions
        {
            ReasoningEffortLevel = ChatReasoningEffortLevel.High
        };

        foreach (var assistantMessage in messages.AsValueEnumerable().Where(m => m.Role == ChatRole.Assistant))
        {
            if (assistantMessage.RawRepresentation is not OpenAI.Chat.ChatMessage chatMessage ||
                assistantMessage.AdditionalProperties?.TryGetValue("reasoning_content", out var reasoningObj) is not true ||
                reasoningObj is not string { Length: > 0 } reasoningContent) continue;

            var patch = new JsonPatch();
            patch.Set("$.reasoning_content"u8.ToArray(), reasoningContent);
            chatMessage.Patch = patch;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// optimized wrapper around MEAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient : DelegatingChatClient
    {
        private static readonly PropertyInfo? ChoicesProperty =
            typeof(StreamingChatCompletionUpdate).GetProperty("Choices", BindingFlags.NonPublic | BindingFlags.Instance);
        private static PropertyInfo? _choiceCountProperty;
        private static PropertyInfo? _choiceIndexerProperty;
        private static PropertyInfo? _choiceDeltaProperty;
        private static PropertyInfo? _deltaPatchProperty;
        private static Func<IEnumerable<ChatMessage>, ChatOptions?, IEnumerable<OpenAI.Chat.ChatMessage>>? _toOpenAIChatMessages;

        private readonly OpenAIKernelMixin _owner;

        /// <summary>
        /// optimized wrapper around MEAI's IChatClient to extract reasoning content from internal properties.
        /// </summary>
        public OptimizedOpenAIApiClient(IChatClient client, OpenAIKernelMixin owner) : base(client)
        {
            _toOpenAIChatMessages ??=
                client.GetType()
                    .GetMethod("ToOpenAIChatMessages", BindingFlags.NonPublic | BindingFlags.Static)
                    .NotNull("Failed to create hook for ToOpenAIChatMessages method. Make sure client is of type OpenAIChatClient.")
                    .CreateDelegate<Func<IEnumerable<ChatMessage>, ChatOptions?, IEnumerable<OpenAI.Chat.ChatMessage>>>();

            _owner = owner;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // snapshot messages for modification
            var messagesList = messages.AsValueEnumerable().ToList();

            // early convert to OpenAI chat messages and store raw representation
            // so that we can modify them in BeforeStreamingRequestAsync if needed
            var openAIMessages = _toOpenAIChatMessages!(messagesList, options);
            foreach (var (original, openai) in messagesList.AsValueEnumerable().Zip(openAIMessages))
            {
                original.RawRepresentation = openai;
            }

            await _owner.BeforeStreamingRequestAsync(messagesList, ref options).ConfigureAwait(false);

            // cache the value to avoid property changes during enumeration
            var isDeepThinkingSupported = _owner.IsDeepThinkingSupported;
            await foreach (var update in base.GetStreamingResponseAsync(messagesList, options, cancellationToken))
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

                    // Cache PropertyInfo for the internal 'Patch' property of the delta.
                    _deltaPatchProperty ??= delta.GetType().GetProperty(
                        "Patch",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    // Extract and process the raw data if it exists.
                    if (_deltaPatchProperty?.GetValue(delta) is not JsonPatch jsonPatch ||
                        !jsonPatch.TryGetValue("$.reasoning_content"u8, out string? reasoningContent))
                    {
                        yield return update;
                        continue;
                    }

                    if (string.IsNullOrEmpty(reasoningContent))
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
    }
}