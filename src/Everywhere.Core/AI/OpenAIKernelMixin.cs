using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
/// An implementation of <see cref="KernelMixin"/> for OpenAI models via Chat Completions.
/// </summary>
public class OpenAIKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    public OpenAIKernelMixin(
        CustomAssistant customAssistant,
        ModelConnection connection,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant, connection)
    {
        // Some models don't need API key (e.g. LM Studio, Official mode)
        AuthenticationPolicy authenticationPolicy = ApiKey.IsNullOrWhiteSpace() ?
            new NoneAuthenticationPolicy() :
            ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(new ApiKeyCredential(ApiKey));

        ChatCompletionService = new OptimizedOpenAIApiClient(
            new ChatClient(
                ModelId,
                authenticationPolicy,
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
    private Task BeforeStreamingRequestAsync(IList<ChatMessage> messages, ref ChatOptions? options)
    {
        // If deep thinking is not supported, skip processing.
        if (!SupportsReasoning) return Task.CompletedTask;

        var opt = options ??= new ChatOptions();
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        options.RawRepresentationFactory ??= _ => RawRepresentationFactory(opt);

        var lastUserMessageIndex = messages.AsValueEnumerable()
            .Select((m, i) => new { Message = m, Index = i })
            .Where(x => x.Message.Role == ChatRole.User)
            .Select(x => x.Index)
            .LastOrDefault(-1);
        if (lastUserMessageIndex == -1 || lastUserMessageIndex == messages.Count - 1) return Task.CompletedTask;

        foreach (var assistantMessage in messages.AsValueEnumerable().Skip(lastUserMessageIndex + 1).Where(m => m.Role == ChatRole.Assistant))
        {
            Debug.Assert(assistantMessage.RawRepresentation is OpenAI.Chat.ChatMessage);
            if (assistantMessage.RawRepresentation is not OpenAI.Chat.ChatMessage chatMessage) continue;

            if (assistantMessage.AdditionalProperties?.TryGetValue("reasoning_content", out var reasoningObj) is not true ||
                reasoningObj is not string reasoningContent)
            {
                reasoningContent = string.Empty; // Set to empty string if not provided, as the field is required
            }

            var patch = new JsonPatch();
            // patch.Set("$.reasoning_content"u8, string.Empty) will throw an exception: Empty encoded value
            // It seems a bug in the JsonPatch implementation, so we need to encode the empty string to bytes manually.
            patch.Set("$.reasoning_content"u8, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reasoningContent)));
            chatMessage.Patch = patch;
        }

        return Task.CompletedTask;
    }

    private static ChatCompletionOptions? RawRepresentationFactory(ChatOptions chatOptions)
    {
        if (chatOptions.AdditionalProperties?.TryGetValue("reasoning_effort_level", out var reasoningEffortLevelObj) is not true) return null;
        if (reasoningEffortLevelObj is not ReasoningEffortLevel reasoningEffortLevel) return null;

        return new ChatCompletionOptions
        {
            Metadata =
            {
                { "thinking", "enabled" }
            },
            ReasoningEffortLevel = reasoningEffortLevel switch
            {
                ReasoningEffortLevel.Minimal => ChatReasoningEffortLevel.Minimal,
                ReasoningEffortLevel.Detailed => ChatReasoningEffortLevel.High,
                _ => (ChatReasoningEffortLevel?)null
            },
        };
    }

    /// <summary>
    /// optimized wrapper around MEAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient(IChatClient client, OpenAIKernelMixin owner) : DelegatingChatClient(client)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // snapshot messages for modification
            var messagesList = messages.AsValueEnumerable().ToList();

            // early convert to OpenAI chat messages and store raw representation
            // so that we can modify them in BeforeStreamingRequestAsync if needed
            var openAIMessages = ToOpenAIChatMessages(null, messagesList, options);
            foreach (var (original, openai) in messagesList.AsValueEnumerable().Zip(openAIMessages))
            {
                original.RawRepresentation = openai;
            }

            await owner.BeforeStreamingRequestAsync(messagesList, ref options).ConfigureAwait(false);

            // cache the value to avoid property changes during enumeration
            await foreach (var update in base.GetStreamingResponseAsync(messagesList, options, cancellationToken))
            {
                // Why you keep reasoning in the fucking internal properties, OpenAI???
                // I'm not a thief, let me access the data! 😭😭😭😭
                if (owner.SupportsReasoning && update is { Text: not { Length: > 0 }, RawRepresentation: StreamingChatCompletionUpdate detail })
                {
                    // Get the value of the internal 'Choices' property.
                    if (GetChoices(detail) is not IEnumerable choices)
                    {
                        yield return update;
                        continue;
                    }

                    var firstChoice = choices.AsValueEnumerable().FirstOrDefault();
                    if (firstChoice is null)
                    {
                        yield return update;
                        continue;
                    }

                    var delta = GetDelta(firstChoice);
                    var jsonPatch = GetPatch(delta);

                    // Extract and process the raw data if it exists.
                    if (!jsonPatch.TryGetValue("$.reasoning_content"u8, out string? reasoningContent))
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

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ToOpenAIChatMessages")]
        private extern static IEnumerable<OpenAI.Chat.ChatMessage> ToOpenAIChatMessages(
            [UnsafeAccessorType("Microsoft.Extensions.AI.OpenAIChatClient, Microsoft.Extensions.AI.OpenAI")]
            object? klass,
            IEnumerable<ChatMessage> inputs,
            ChatOptions? chatOptions);

        private const string FuckingInternalChoiceTypeName = "OpenAI.Chat.InternalCreateChatCompletionStreamResponseChoice, OpenAI";
        private const string FuckingInternalDeltaTypeName = "OpenAI.Chat.InternalChatCompletionStreamResponseDelta, OpenAI";
        private const string FuckingInternalChoiceListTypeName =
            $"System.Collections.Generic.IReadOnlyList`1[[{FuckingInternalChoiceTypeName}]], System.Private.CoreLib";

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Choices")]
        [return: UnsafeAccessorType(FuckingInternalChoiceListTypeName)]
        private extern static object GetChoices(StreamingChatCompletionUpdate update);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Delta")]
        [return: UnsafeAccessorType(FuckingInternalDeltaTypeName)]
        private extern static object GetDelta([UnsafeAccessorType(FuckingInternalChoiceTypeName)] object choice);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_patch")]
        private extern static ref JsonPatch GetPatch([UnsafeAccessorType(FuckingInternalDeltaTypeName)] object delta);
    }

    private sealed class NoneAuthenticationPolicy : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNext(message, pipeline, currentIndex);

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNextAsync(message, pipeline, currentIndex);
    }
}