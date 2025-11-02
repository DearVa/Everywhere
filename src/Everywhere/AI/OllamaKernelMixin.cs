using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using Everywhere.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using OllamaSharp;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Ollama models.
/// </summary>
public sealed partial class OllamaKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null) =>
        new OllamaPromptExecutionSettings
        {
            Temperature = (float)_customAssistant.Temperature,
            TopP = (float)_customAssistant.TopP,
            FunctionChoiceBehavior = functionChoiceBehavior
        };

    private readonly OllamaApiClient _client;
    private readonly HttpClient _ollamaHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaKernelMixin"/> class.
    /// </summary>
    public OllamaKernelMixin(CustomAssistant customAssistant) : base(customAssistant)
    {
        _ollamaHttpClient = ProxyHttpClientFactory.CreateHttpClient();
        _ollamaHttpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);
        _client = new OllamaApiClient(_ollamaHttpClient, ModelId);
        ChatCompletionService = new OptimizedOllamaApiClient(_client, this).AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
        _ollamaHttpClient.Dispose();
    }

    private sealed partial class OptimizedOllamaApiClient(OllamaApiClient client, OllamaKernelMixin owner) : IChatClient
    {
        private IChatClient ChatClient => client;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await ChatClient.GetResponseAsync(messages, options, cancellationToken);
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

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!owner.IsDeepThinkingSupported)
            {
                await foreach (var update in ChatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            var reasoningState = 0; // 0: not started, 1: started, 2: reasoning, 3: done
            var processedContents = new List<AIContent>();
            await foreach (var update in ChatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
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

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return ChatClient.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            client.Dispose();
        }

        [GeneratedRegex(@"<think>(.*?)</think>(.*)", RegexOptions.Singleline)]
        private static partial Regex ReasoningRegex();
    }
}