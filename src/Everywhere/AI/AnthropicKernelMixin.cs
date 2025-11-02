using System;
using System.Net.Http;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Anthropic models.
/// </summary>
public sealed class AnthropicKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null) => new()
    {
        ModelId = ModelId,
        FunctionChoiceBehavior = functionChoiceBehavior
    };

    private readonly OptimizedChatClient _client;
    private readonly HttpClient _anthropicHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicKernelMixin"/> class.
    /// </summary>
    public AnthropicKernelMixin(CustomAssistant customAssistant) : base(customAssistant)
    {
        _anthropicHttpClient = ProxyHttpClientFactory.CreateHttpClient();
        _anthropicHttpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);
        var anthropicClient = new AnthropicClient(new APIAuthentication(ApiKey), _anthropicHttpClient)
        {
            ApiUrlFormat = Endpoint + "/{0}/{1}"
        };
        var messagesEndpoint = anthropicClient.Messages;
        _client = new OptimizedChatClient(customAssistant, messagesEndpoint);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
        _anthropicHttpClient.Dispose();
    }

    private sealed class OptimizedChatClient(CustomAssistant customAssistant, MessagesEndpoint anthropicClient) : IChatClient
    {
        private void BuildOptions(ref ChatOptions? options)
        {
            options ??= new ChatOptions();

            double? temperature = customAssistant.Temperature.IsCustomValueSet ? customAssistant.Temperature.ActualValue : null;
            double? topP = customAssistant.TopP.IsCustomValueSet ? customAssistant.TopP.ActualValue : null;
            double? presencePenalty = customAssistant.PresencePenalty.IsCustomValueSet ? customAssistant.PresencePenalty.ActualValue : null;
            double? frequencyPenalty = customAssistant.FrequencyPenalty.IsCustomValueSet ? customAssistant.FrequencyPenalty.ActualValue : null;

            if (temperature is not null) options.Temperature = (float)temperature.Value;
            if (topP is not null) options.TopP = (float)topP.Value;
            if (presencePenalty is not null) options.PresencePenalty = (float)presencePenalty.Value;
            if (frequencyPenalty is not null) options.FrequencyPenalty = (float)frequencyPenalty.Value;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return ((IChatClient)anthropicClient).GetResponseAsync(messages, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return ((IChatClient)anthropicClient).GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return ((IChatClient)anthropicClient).GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            ((IDisposable)anthropicClient).Dispose();
        }
    }
}