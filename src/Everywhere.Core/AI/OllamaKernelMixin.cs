using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;
using OllamaSharp;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Ollama models.
/// </summary>
public sealed partial class OllamaKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OllamaApiClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaKernelMixin"/> class.
    /// </summary>
    public OllamaKernelMixin(CustomAssistant customAssistant, HttpClient httpClient) : base(customAssistant)
    {
        httpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);
        _client = new OllamaApiClient(httpClient, ModelId);
        ChatCompletionService = new OptimizedOllamaApiClient(this, _client).AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed partial class OptimizedOllamaApiClient(OllamaKernelMixin owner, OllamaApiClient client) : DelegatingChatClient(client)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            if (!owner.IsDeepThinkingSupported) return response;

            // handle reasoning in non-streaming mode, only actual response
            // use regex to extract parts <think>[reasoning]</think>[response]
            // then return only response part with reasoning property if exists
            var text = response.Text;
            var regex = ReasoningRegex();
            var match = regex.Match(text);
            if (!match.Success) return response;

            return new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextReasoningContent(match.Groups[1].Value.Trim()),
                        new TextContent(match.Groups[2].Value.Trim())
                    ]));
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!owner.IsDeepThinkingSupported)
            {
                await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            var reasoningState = 0; // 0: not started, 1: started, 2: reasoning, 3: done
            var processedContents = new List<AIContent>();
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                processedContents.Clear();
                var hasReasoningContent = false;

                foreach (var content in update.Contents)
                {
                    if (content is TextContent textContent)
                    {
                        switch (reasoningState)
                        {
                            case 0 when textContent.Text == "<think>":
                            {
                                reasoningState = 1;
                                // ignore <think> token
                                break;
                            }
                            case 1 when textContent.Text.IsNullOrWhiteSpace():
                            {
                                break;
                            }
                            case 1:
                            {
                                reasoningState = 2;
                                processedContents.Add(
                                    new TextContent(textContent.Text)
                                    {
                                        AdditionalProperties = ReasoningProperties
                                    });
                                hasReasoningContent = true;
                                break;
                            }
                            case 2 when textContent.Text == "</think>":
                            {
                                reasoningState = 3;
                                break;
                            }
                            case 2:
                            {
                                processedContents.Add(
                                    new TextContent(textContent.Text)
                                    {
                                        AdditionalProperties = ReasoningProperties
                                    });
                                hasReasoningContent = true;
                                break;
                            }
                            default:
                            {
                                processedContents.Add(textContent);
                                break;
                            }
                        }
                    }
                    else
                    {
                        processedContents.Add(content);
                    }
                }

                if (processedContents.Count == 0)
                {
                    continue;
                }

                update.Contents = processedContents;
                if (hasReasoningContent) update.AdditionalProperties = ApplyReasoningProperties(update.AdditionalProperties);

                yield return update;
            }
        }

        [GeneratedRegex(@"<think>(.*?)</think>(.*)", RegexOptions.Singleline)]
        private static partial Regex ReasoningRegex();
    }
}