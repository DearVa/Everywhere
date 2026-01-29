using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;

#pragma warning disable IDE0130 // Namespace does not match folder structure

namespace Microsoft.Extensions.AI;

public static class AnthropicClientExtensions
{
    /// <summary>Gets an <see cref="IChatClient"/> for use with this <see cref="IAnthropicClient"/>.</summary>
    /// <param name="client">The client.</param>
    /// <param name="defaultModelId">
    /// The default ID of the model to use.
    /// If <see langword="null"/>, it must be provided per request via <see cref="ChatOptions.ModelId"/>.
    /// </param>
    /// <param name="defaultMaxOutputTokens">
    /// The default maximum number of tokens to generate in a response.
    /// This may be overridden with <see cref="ChatOptions.MaxOutputTokens"/>.
    /// If no value is provided for this parameter or in <see cref="ChatOptions"/>, a default maximum will be used.
    /// </param>
    /// <returns>An <see cref="IChatClient"/> that can be used to converse via the <see cref="IAnthropicClient"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static IChatClient AsIChatClient(
        this IAnthropicClient client,
        string? defaultModelId = null,
        int? defaultMaxOutputTokens = null
    )
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (defaultMaxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultMaxOutputTokens),
                "Default max tokens must be greater than zero."
            );
        }

        return new AnthropicChatClient(client, defaultModelId, defaultMaxOutputTokens);
    }

    /// <summary>Creates an <see cref="AITool"/> to represent a raw <see cref="ToolUnion"/>.</summary>
    /// <param name="tool">The tool to wrap as an <see cref="AITool"/>.</param>
    /// <returns>The <paramref name="tool"/> wrapped as an <see cref="AITool"/>.</returns>
    /// <remarks>
    /// <para>
    /// The returned tool is only suitable for use with the <see cref="IChatClient"/> returned by
    /// <see cref="AsIChatClient"/> (or <see cref="IChatClient"/>s that delegate
    /// to such an instance). It is likely to be ignored by any other <see cref="IChatClient"/> implementation.
    /// </para>
    /// <para>
    /// When a tool has a corresponding <see cref="AITool"/>-derived type already defined in Microsoft.Extensions.AI,
    /// such as <see cref="AIFunction"/>, <see cref="HostedWebSearchTool"/>, <see cref="HostedMcpServerTool"/>, or
    /// <see cref="HostedFileSearchTool"/>, those types should be preferred instead of this method, as they are more portable,
    /// capable of being respected by any <see cref="IChatClient"/> implementation. This method does not attempt to
    /// map the supplied <see cref="ToolUnion"/> to any of those types, it simply wraps it as-is:
    /// the <see cref="IChatClient"/> returned by <see cref="AsIChatClient"/> will
    /// be able to unwrap the <see cref="ToolUnion"/> when it processes the list of tools.
    /// </para>
    /// </remarks>
    public static AITool AsAITool(this ToolUnion tool)
    {
        if (tool is null)
        {
            throw new ArgumentNullException(nameof(tool));
        }

        return new ToolUnionAITool(tool);
    }

    private sealed class AnthropicChatClient(
        IAnthropicClient anthropicClient,
        string? defaultModelId,
        int? defaultMaxTokens
    ) : IChatClient
    {
        private const int DefaultMaxTokens = 1024;

        private readonly IAnthropicClient _anthropicClient = anthropicClient;
        private readonly string? _defaultModelId = defaultModelId;
        private readonly int _defaultMaxTokens = defaultMaxTokens ?? DefaultMaxTokens;
        private ChatClientMetadata? _metadata;

        /// <inheritdoc />
        void IDisposable.Dispose() { }

        /// <inheritdoc />
        public object? GetService(System.Type serviceType, object? serviceKey = null)
        {
            if (serviceType is null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            if (serviceKey is not null)
            {
                return null;
            }

            if (serviceType == typeof(ChatClientMetadata))
            {
                return _metadata ??= new(
                    "anthropic",
                    new Uri(_anthropicClient.BaseUrl),
                    _defaultModelId
                );
            }

            if (serviceType.IsInstanceOfType(_anthropicClient))
            {
                return _anthropicClient;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            if (messages is null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            List<MessageParam> messageParams = CreateMessageParams(
                messages,
                out List<TextBlockParam>? systemMessages
            );
            MessageCreateParams createParams = GetMessageCreateParams(
                messageParams,
                systemMessages,
                options
            );

            var createResult = await _anthropicClient.Messages.Create(
                createParams,
                cancellationToken
            );

            ChatMessage m = new(ChatRole.Assistant, [.. createResult.Content.Select(ToAIContent)])
            {
                CreatedAt = DateTimeOffset.UtcNow,
                MessageId = createResult.ID,
            };

            return new(m)
            {
                CreatedAt = m.CreatedAt,
                FinishReason = ToFinishReason(createResult.StopReason),
                ModelId = createResult.Model.Raw() ?? createParams.Model.Raw(),
                RawRepresentation = createResult,
                ResponseId = m.MessageId,
                Usage = createResult.Usage is { } usage ? ToUsageDetails(usage) : null,
            };
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            if (messages is null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            List<MessageParam> messageParams = CreateMessageParams(
                messages,
                out List<TextBlockParam>? systemMessages
            );
            MessageCreateParams createParams = GetMessageCreateParams(
                messageParams,
                systemMessages,
                options
            );

            string? messageId = null;
            string? modelID = null;
            UsageDetails? usageDetails = null;
            ChatFinishReason? finishReason = null;
            Dictionary<long, StreamingFunctionData>? streamingFunctions = null;

            await foreach (
                var createResult in _anthropicClient
                    .Messages.CreateStreaming(createParams, cancellationToken)
                    .WithCancellation(cancellationToken)
            )
            {
                List<AIContent> contents = [];

                switch (createResult.Value)
                {
                    case RawMessageStartEvent rawMessageStart:
                        if (string.IsNullOrWhiteSpace(messageId))
                        {
                            messageId = rawMessageStart.Message.ID;
                        }

                        if (string.IsNullOrWhiteSpace(modelID))
                        {
                            modelID = rawMessageStart.Message.Model;
                        }

                        if (rawMessageStart.Message.Usage is { } usage)
                        {
                            usageDetails = ToUsageDetails(usage);
                        }
                        break;

                    case RawMessageDeltaEvent rawMessageDelta:
                        finishReason = ToFinishReason(rawMessageDelta.Delta.StopReason);
                        if (rawMessageDelta.Usage is { } deltaUsage)
                        {
                            UsageDetails current = ToUsageDetails(deltaUsage);
                            if (usageDetails is null)
                            {
                                usageDetails = current;
                            }
                            else
                            {
                                usageDetails.InputTokenCount = Math.Max(
                                    usageDetails.InputTokenCount ?? 0,
                                    current.InputTokenCount ?? 0
                                );
                                usageDetails.OutputTokenCount = Math.Max(
                                    usageDetails.OutputTokenCount ?? 0,
                                    current.OutputTokenCount ?? 0
                                );
                                usageDetails.TotalTokenCount = Math.Max(
                                    usageDetails.TotalTokenCount ?? 0,
                                    current.TotalTokenCount ?? 0
                                );
                                usageDetails.CachedInputTokenCount = Math.Max(
                                    usageDetails.CachedInputTokenCount ?? 0,
                                    current.CachedInputTokenCount ?? 0
                                );
                                usageDetails.ReasoningTokenCount = Math.Max(
                                    usageDetails.ReasoningTokenCount ?? 0,
                                    current.ReasoningTokenCount ?? 0
                                );

                                if (usageDetails.AdditionalCounts is { } additionalCounts)
                                {
                                    if (current.AdditionalCounts is null)
                                    {
                                        current.AdditionalCounts = new(additionalCounts);
                                    }
                                    else
                                    {
                                        foreach (var kvp in additionalCounts)
                                        {
                                            current.AdditionalCounts[kvp.Key] = current.AdditionalCounts.TryGetValue(kvp.Key, out var existingValue) ?
                                                Math.Max(existingValue, kvp.Value) :
                                                kvp.Value;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case RawContentBlockStartEvent contentBlockStart:
                        switch (contentBlockStart.ContentBlock.Value)
                        {
                            case TextBlock text:
                                contents.Add(
                                    new TextContent(text.Text) { RawRepresentation = text }
                                );
                                break;

                            case ThinkingBlock thinking:
                                contents.Add(
                                    new TextReasoningContent(thinking.Thinking)
                                    {
                                        ProtectedData = thinking.Signature,
                                        RawRepresentation = thinking,
                                    }
                                );
                                break;

                            case RedactedThinkingBlock redactedThinking:
                                contents.Add(
                                    new TextReasoningContent(string.Empty)
                                    {
                                        ProtectedData = redactedThinking.Data,
                                        RawRepresentation = redactedThinking,
                                    }
                                );
                                break;

                            case ToolUseBlock toolUse:
                                streamingFunctions ??= [];
                                streamingFunctions[contentBlockStart.Index] = new()
                                {
                                    CallId = toolUse.ID,
                                    Name = toolUse.Name,
                                };
                                break;
                        }
                        break;

                    case RawContentBlockDeltaEvent contentBlockDelta:
                        switch (contentBlockDelta.Delta.Value)
                        {
                            case TextDelta textDelta:
                                contents.Add(
                                    new TextContent(textDelta.Text)
                                    {
                                        RawRepresentation = textDelta,
                                    }
                                );
                                break;

                            case InputJsonDelta inputDelta:
                                if (
                                    streamingFunctions is not null
                                    && streamingFunctions.TryGetValue(
                                        contentBlockDelta.Index,
                                        out StreamingFunctionData? functionData
                                    )
                                )
                                {
                                    functionData.Arguments.Append(inputDelta.PartialJson);
                                }
                                break;

                            case ThinkingDelta thinkingDelta:
                                contents.Add(
                                    new TextReasoningContent(thinkingDelta.Thinking)
                                    {
                                        RawRepresentation = thinkingDelta,
                                    }
                                );
                                break;

                            case SignatureDelta signatureDelta:
                                contents.Add(
                                    new TextReasoningContent(null)
                                    {
                                        ProtectedData = signatureDelta.Signature,
                                        RawRepresentation = signatureDelta,
                                    }
                                );
                                break;
                        }
                        break;

                    case RawContentBlockStopEvent contentBlockStop:
                        if (streamingFunctions is not null)
                        {
                            foreach (var sf in streamingFunctions)
                            {
                                contents.Add(
                                    FunctionCallContent.CreateFromParsedArguments(
                                        sf.Value.Arguments.ToString(),
                                        sf.Value.CallId,
                                        sf.Value.Name,
                                        json =>
                                            (Dictionary<string, object?>?)
                                                JsonSerializer.Deserialize(
                                                    json,
                                                    AIJsonUtilities.DefaultOptions.GetTypeInfo(
                                                        typeof(Dictionary<string, object?>)
                                                    )
                                                )
                                    )
                                );
                            }

                            streamingFunctions.Clear();
                        }
                        break;
                }

                yield return new(ChatRole.Assistant, contents)
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    FinishReason = finishReason,
                    MessageId = messageId,
                    ModelId = modelID,
                    RawRepresentation = createResult,
                    ResponseId = messageId,
                };
            }

            if (usageDetails is not null)
            {
                yield return new(ChatRole.Assistant, [new UsageContent(usageDetails)])
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    FinishReason = finishReason,
                    MessageId = messageId,
                    ModelId = modelID,
                    ResponseId = messageId,
                };
            }
        }

        private static List<MessageParam> CreateMessageParams(
            IEnumerable<ChatMessage> messages,
            out List<TextBlockParam>? systemMessages
        )
        {
            List<MessageParam> messageParams = [];
            systemMessages = null;

            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.System)
                {
                    foreach (AIContent content in message.Contents)
                    {
                        if (content is TextContent tc)
                        {
                            (systemMessages ??= []).Add(new() { Text = tc.Text });
                        }
                    }

                    continue;
                }

                List<ContentBlockParam> contents = [];

                foreach (AIContent content in message.Contents)
                {
                    switch (content)
                    {
                        case AIContent ac when ac.RawRepresentation is ContentBlockParam rawContent:
                            contents.Add(rawContent);
                            break;

                        case TextContent tc:
                            string text = tc.Text;
                            if (message.Role == ChatRole.Assistant)
                            {
                                text = text.TrimEnd();
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    contents.Add(new TextBlockParam() { Text = text });
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                contents.Add(new TextBlockParam() { Text = text });
                            }
                            break;

                        case TextReasoningContent trc when !string.IsNullOrEmpty(trc.Text):
                            contents.Add(
                                new ThinkingBlockParam()
                                {
                                    Thinking = trc.Text,
                                    Signature = trc.ProtectedData ?? string.Empty,
                                }
                            );
                            break;

                        case TextReasoningContent trc when !string.IsNullOrEmpty(trc.ProtectedData):
                            contents.Add(
                                new RedactedThinkingBlockParam() { Data = trc.ProtectedData! }
                            );
                            break;

                        case DataContent dc when dc.HasTopLevelMediaType("image"):
                            contents.Add(
                                new ImageBlockParam()
                                {
                                    Source = new(
                                        new Base64ImageSource()
                                        {
                                            Data = dc.Base64Data.ToString(),
                                            MediaType = dc.MediaType,
                                        }
                                    ),
                                }
                            );
                            break;

                        case DataContent dc
                            when string.Equals(
                                dc.MediaType,
                                "application/pdf",
                                StringComparison.OrdinalIgnoreCase
                            ):
                            contents.Add(
                                new DocumentBlockParam()
                                {
                                    Source = new(
                                        new Base64PdfSource() { Data = dc.Base64Data.ToString() }
                                    ),
                                }
                            );
                            break;

                        case DataContent dc when dc.HasTopLevelMediaType("text"):
                            contents.Add(
                                new DocumentBlockParam()
                                {
                                    Source = new(
                                        new PlainTextSource()
                                        {
                                            Data = Encoding.UTF8.GetString(dc.Data.ToArray()),
                                        }
                                    ),
                                }
                            );
                            break;

                        case UriContent uc when uc.HasTopLevelMediaType("image"):
                            contents.Add(
                                new ImageBlockParam()
                                {
                                    Source = new(new UrlImageSource() { Url = uc.Uri.AbsoluteUri }),
                                }
                            );
                            break;

                        case UriContent uc
                            when string.Equals(
                                uc.MediaType,
                                "application/pdf",
                                StringComparison.OrdinalIgnoreCase
                            ):
                            contents.Add(
                                new DocumentBlockParam()
                                {
                                    Source = new(new UrlPdfSource() { Url = uc.Uri.AbsoluteUri }),
                                }
                            );
                            break;

                        case FunctionCallContent fcc:
                            contents.Add(
                                new ToolUseBlockParam()
                                {
                                    ID = fcc.CallId,
                                    Name = fcc.Name,
                                    Input =
                                        fcc.Arguments?.ToDictionary(
                                            e => e.Key,
                                            e =>
                                                e.Value is JsonElement je
                                                    ? je
                                                    : JsonSerializer.SerializeToElement(
                                                        e.Value,
                                                        AIJsonUtilities.DefaultOptions.GetTypeInfo(
                                                            typeof(object)
                                                        )
                                                    )
                                        ) ?? [],
                                }
                            );
                            break;

                        case FunctionResultContent frc:
                            ToolResultBlockParamContent result = frc.Result switch
                            {
                                ToolResultBlockParamContent trbpc => trbpc,

                                AIContent aiContent => new(ToResultBlocks([aiContent])),

                                IEnumerable<AIContent> aiContents => new(
                                    ToResultBlocks(aiContents)
                                ),

                                _ => new(
                                    JsonSerializer.Serialize(
                                        frc.Result,
                                        AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(object))
                                    )
                                ),
                            };

                            static IReadOnlyList<Block> ToResultBlocks(
                                IEnumerable<AIContent> aiContents
                            )
                            {
                                List<Block> blocks = [];
                                foreach (AIContent ac in aiContents)
                                {
                                    blocks.Add(
                                        ac switch
                                        {
                                            AIContent ai
                                                when ai.RawRepresentation is Block rawBlock =>
                                                rawBlock,

                                            TextContent tc => new Block(
                                                new TextBlockParam() { Text = tc.Text }
                                            ),

                                            DataContent dc when dc.HasTopLevelMediaType("image") =>
                                                new Block(
                                                    new ImageBlockParam()
                                                    {
                                                        Source = new(
                                                            new Base64ImageSource()
                                                            {
                                                                Data = dc.Base64Data.ToString(),
                                                                MediaType = dc.MediaType,
                                                            }
                                                        ),
                                                    }
                                                ),

                                            DataContent dc
                                                when string.Equals(
                                                    dc.MediaType,
                                                    "application/pdf",
                                                    StringComparison.OrdinalIgnoreCase
                                                ) => new Block(
                                                new DocumentBlockParam()
                                                {
                                                    Source = new(
                                                        new Base64PdfSource()
                                                        {
                                                            Data = dc.Base64Data.ToString(),
                                                        }
                                                    ),
                                                }
                                            ),

                                            DataContent dc when dc.HasTopLevelMediaType("text") =>
                                                new Block(
                                                    new DocumentBlockParam()
                                                    {
                                                        Source = new(
                                                            new PlainTextSource()
                                                            {
#if NET
                                                                Data = Encoding.UTF8.GetString(
                                                                    dc.Data.Span
                                                                ),
#else
                                                                Data = Encoding.UTF8.GetString(
                                                                    dc.Data.ToArray()
                                                                ),
#endif
                                                            }
                                                        ),
                                                    }
                                                ),

                                            UriContent uc when uc.HasTopLevelMediaType("image") =>
                                                new Block(
                                                    new ImageBlockParam()
                                                    {
                                                        Source = new(
                                                            new UrlImageSource()
                                                            {
                                                                Url = uc.Uri.AbsoluteUri,
                                                            }
                                                        ),
                                                    }
                                                ),

                                            UriContent uc
                                                when string.Equals(
                                                    uc.MediaType,
                                                    "application/pdf",
                                                    StringComparison.OrdinalIgnoreCase
                                                ) => new Block(
                                                new DocumentBlockParam()
                                                {
                                                    Source = new(
                                                        new UrlPdfSource()
                                                        {
                                                            Url = uc.Uri.AbsoluteUri,
                                                        }
                                                    ),
                                                }
                                            ),

                                            _ => new Block(
                                                new TextBlockParam()
                                                {
                                                    Text = JsonSerializer.Serialize(
                                                        ac,
                                                        AIJsonUtilities.DefaultOptions.GetTypeInfo(
                                                            typeof(object)
                                                        )
                                                    ),
                                                }
                                            ),
                                        }
                                    );
                                }

                                return blocks;
                            }

                            contents.Add(
                                new ToolResultBlockParam()
                                {
                                    ToolUseID = frc.CallId,
                                    IsError = frc.Exception is not null,
                                    Content = result,
                                }
                            );
                            break;
                    }
                }

                if (contents.Count == 0)
                {
                    continue;
                }

                messageParams.Add(
                    new()
                    {
                        Role = message.Role == ChatRole.Assistant ? Role.Assistant : Role.User,
                        Content = contents,
                    }
                );
            }

            if (messageParams.Count == 0)
            {
                messageParams.Add(new() { Role = Role.User, Content = new("\u200b") }); // zero-width space
            }

            return messageParams;
        }

        private MessageCreateParams GetMessageCreateParams(
            List<MessageParam> messages,
            List<TextBlockParam>? systemMessages,
            ChatOptions? options
        )
        {
            // Get the initial MessageCreateParams, either with a raw representation provided by the options
            // or with only the required properties set.
            MessageCreateParams? createParams =
                options?.RawRepresentationFactory?.Invoke(this) as MessageCreateParams;
            if (createParams is not null)
            {
                // Merge any messages preconfigured on the params with the ones provided to the IChatClient.
                createParams = createParams with
                {
                    Messages = [.. createParams.Messages, .. messages],
                };
            }
            else
            {
                createParams = new MessageCreateParams()
                {
                    MaxTokens = options?.MaxOutputTokens ?? _defaultMaxTokens,
                    Messages = messages,
                    Model =
                        options?.ModelId
                        ?? _defaultModelId
                        ?? throw new InvalidOperationException(
                            "Model ID must be specified either in ChatOptions or as the default for the client."
                        ),
                };
            }

            // Handle any other options to propagate to the create params.
            if (options is not null)
            {
                if (options.Instructions is { } instructions)
                {
                    (systemMessages ??= []).Add(new TextBlockParam() { Text = instructions });
                }

                if (options.StopSequences is { Count: > 0 } stopSequences)
                {
                    createParams = createParams.StopSequences is { } existingSequences
                        ? createParams with
                        {
                            StopSequences = [.. existingSequences, .. stopSequences],
                        }
                        : createParams with
                        {
                            StopSequences = [.. stopSequences],
                        };
                }

                if (createParams.Temperature is null && options.Temperature is { } temperature)
                {
                    createParams = createParams with { Temperature = temperature };
                }

                if (createParams.TopK is null && options.TopK is { } topK)
                {
                    createParams = createParams with { TopK = topK };
                }

                if (createParams.TopP is null && options.TopP is { } topP)
                {
                    createParams = createParams with { TopP = topP };
                }

                if (options.Tools is { } tools)
                {
                    List<ToolUnion>? createdTools = createParams.Tools?.ToList();
                    foreach (var tool in tools)
                    {
                        switch (tool)
                        {
                            case ToolUnionAITool raw:
                                (createdTools ??= []).Add(raw.Tool);
                                break;

                            case AIFunctionDeclaration af:
                                Dictionary<string, JsonElement> properties = [];
                                List<string> required = [];
                                JsonElement inputSchema = af.JsonSchema;
                                if (inputSchema.ValueKind is JsonValueKind.Object)
                                {
                                    if (
                                        inputSchema.TryGetProperty(
                                            "properties",
                                            out JsonElement propsElement
                                        )
                                        && propsElement.ValueKind is JsonValueKind.Object
                                    )
                                    {
                                        foreach (JsonProperty p in propsElement.EnumerateObject())
                                        {
                                            properties[p.Name] = p.Value;
                                        }
                                    }

                                    if (
                                        inputSchema.TryGetProperty(
                                            "required",
                                            out JsonElement reqElement
                                        )
                                        && reqElement.ValueKind is JsonValueKind.Array
                                    )
                                    {
                                        foreach (JsonElement r in reqElement.EnumerateArray())
                                        {
                                            if (
                                                r.ValueKind is JsonValueKind.String
                                                && r.GetString() is { } s
                                                && !string.IsNullOrWhiteSpace(s)
                                            )
                                            {
                                                required.Add(s);
                                            }
                                        }
                                    }
                                }

                                (createdTools ??= []).Add(
                                    new Tool()
                                    {
                                        Name = af.Name,
                                        Description = af.Description,
                                        InputSchema = new InputSchema()
                                        {
                                            Properties = properties,
                                            Required = required,
                                        },
                                    }
                                );
                                break;

                            case HostedWebSearchTool:
                                (createdTools ??= []).Add(new WebSearchTool20250305());
                                break;
                        }
                    }

                    if (createdTools?.Count > 0)
                    {
                        createParams = createParams with { Tools = createdTools };
                    }
                }

                if (createParams.ToolChoice is null && options.ToolMode is { } toolMode)
                {
                    ToolChoice? toolChoice =
                        toolMode is AutoChatToolMode
                            ? new ToolChoiceAuto()
                            {
                                DisableParallelToolUse = !options.AllowMultipleToolCalls,
                            }
                        : toolMode is NoneChatToolMode ? new ToolChoiceNone()
                        : toolMode is RequiredChatToolMode
                            ? new ToolChoiceAny()
                            {
                                DisableParallelToolUse = !options.AllowMultipleToolCalls,
                            }
                        : (ToolChoice?)null;
                    if (toolChoice is not null)
                    {
                        createParams = createParams with { ToolChoice = toolChoice };
                    }
                }
            }

            if (systemMessages is not null)
            {
                if (createParams.System is { } existingSystem)
                {
                    if (existingSystem.Value is string existingMessage)
                    {
                        systemMessages.Insert(0, new TextBlockParam() { Text = existingMessage });
                    }
                    else if (existingSystem.Value is IReadOnlyList<TextBlockParam> existingMessages)
                    {
                        systemMessages.InsertRange(0, existingMessages);
                    }
                }

                createParams = createParams with { System = systemMessages };
            }

            return createParams;
        }

        private static UsageDetails ToUsageDetails(Usage usage) =>
            ToUsageDetails(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheCreationInputTokens,
                usage.CacheReadInputTokens,
                usage.ServerToolUse
            );

        private static UsageDetails ToUsageDetails(MessageDeltaUsage usage) =>
            ToUsageDetails(
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheCreationInputTokens,
                usage.CacheReadInputTokens,
                usage.ServerToolUse
            );

        private static UsageDetails ToUsageDetails(
            long? inputTokens,
            long? outputTokens,
            long? cacheCreationInputTokens,
            long? cacheReadInputTokens,
            ServerToolUsage? serverToolUsage
        )
        {
            UsageDetails usageDetails = new()
            {
                // From https://platform.claude.com/docs/en/build-with-claude/prompt-caching:
                // "To calculate total input tokens:"
                // "total_input_tokens = cache_read_input_tokens + cache_creation_input_tokens + input_tokens"
                InputTokenCount = NullableSum(
                    NullableSum(cacheReadInputTokens, cacheCreationInputTokens),
                    inputTokens
                ),

                CachedInputTokenCount = cacheReadInputTokens,

                OutputTokenCount = outputTokens,
            };

            usageDetails.TotalTokenCount = NullableSum(
                usageDetails.InputTokenCount,
                usageDetails.OutputTokenCount
            );

            if (cacheCreationInputTokens is > 0)
            {
                (usageDetails.AdditionalCounts ??= [])[nameof(Usage.CacheCreationInputTokens)] =
                    cacheCreationInputTokens.Value;
            }

            if (serverToolUsage?.WebSearchRequests is > 0)
            {
                (usageDetails.AdditionalCounts ??= [])[nameof(ServerToolUsage.WebSearchRequests)] =
                    serverToolUsage.WebSearchRequests;
            }

            return usageDetails;

            static long? NullableSum(long? a, long? b) =>
                a is not null || b is not null ? (a ?? 0) + (b ?? 0) : null;
        }

        private static ChatFinishReason? ToFinishReason(ApiEnum<string, StopReason>? stopReason) =>
            stopReason?.Value() switch
            {
                null => null,
                StopReason.Refusal => ChatFinishReason.ContentFilter,
                StopReason.MaxTokens => ChatFinishReason.Length,
                StopReason.ToolUse => ChatFinishReason.ToolCalls,
                _ => ChatFinishReason.Stop,
            };

        private static AIContent ToAIContent(ContentBlock block)
        {
            switch (block.Value)
            {
                case TextBlock text:
                    TextContent tc = new(text.Text) { RawRepresentation = text };

                    if (text.Citations is { Count: > 0 })
                    {
                        tc.Annotations =
                        [
                            .. text.Citations.Select(ToAIAnnotation).OfType<AIAnnotation>(),
                        ];
                    }

                    return tc;

                case ThinkingBlock thinking:
                    return new TextReasoningContent(thinking.Thinking)
                    {
                        ProtectedData = thinking.Signature,
                        RawRepresentation = thinking,
                    };

                case RedactedThinkingBlock redactedThinking:
                    return new TextReasoningContent(string.Empty)
                    {
                        ProtectedData = redactedThinking.Data,
                        RawRepresentation = redactedThinking,
                    };

                case ToolUseBlock toolUse:
                    return new FunctionCallContent(
                        toolUse.ID,
                        toolUse.Name,
                        toolUse.RawData.TryGetValue("input", out JsonElement element)
                            ? (Dictionary<string, object?>?)
                                JsonSerializer.Deserialize(
                                    element,
                                    AIJsonUtilities.DefaultOptions.GetTypeInfo(
                                        typeof(Dictionary<string, object?>)
                                    )
                                )
                            : null
                    )
                    {
                        RawRepresentation = toolUse,
                    };

                default:
                    return new AIContent() { RawRepresentation = block.Value };
            }
        }

        private static AIAnnotation? ToAIAnnotation(TextCitation citation)
        {
            CitationAnnotation annotation = new()
            {
                Title = citation.Title ?? citation.DocumentTitle,
                Snippet = citation.CitedText,
                FileId = citation.FileID,
            };

            if (citation.TryPickCitationsWebSearchResultLocation(out var webSearchLocation))
            {
                annotation.Url = Uri.TryCreate(
                    webSearchLocation.Url,
                    UriKind.Absolute,
                    out Uri? url
                )
                    ? url
                    : null;
            }
            else if (citation.TryPickCitationsSearchResultLocation(out var searchLocation))
            {
                annotation.Url = Uri.TryCreate(
                    searchLocation.Source,
                    UriKind.Absolute,
                    out Uri? url
                )
                    ? url
                    : null;
            }

            return annotation;
        }

        private sealed class StreamingFunctionData
        {
            public string CallId { get; set; } = "";
            public string Name { get; set; } = "";
            public StringBuilder Arguments { get; } = new();
        }
    }

    private sealed class ToolUnionAITool(ToolUnion tool) : AITool
    {
        public ToolUnion Tool => tool;

        public override string Name => tool.Value?.GetType().Name ?? base.Name;

        public override object? GetService(System.Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType?.IsInstanceOfType(tool) is true
                ? tool
                : base.GetService(serviceType!, serviceKey);
    }
}
