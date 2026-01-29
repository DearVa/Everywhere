using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Anthropic models.
/// </summary>
public sealed class AnthropicKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OptimizedChatClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicKernelMixin"/> class.
    /// </summary>
    public AnthropicKernelMixin(CustomAssistant customAssistant, HttpClient httpClient) : base(customAssistant)
    {
        var anthropicClient = new AnthropicClient(new ClientOptions
        {
            ApiKey = ApiKey,
            HttpClient = httpClient,
            BaseUrl = Endpoint
        }).AsIChatClient(defaultModelId: ModelId);
        _client = new OptimizedChatClient(customAssistant, anthropicClient);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed class OptimizedChatClient(CustomAssistant customAssistant, IChatClient anthropicClient) : DelegatingChatClient(anthropicClient)
    {
        private void BuildOptions(ref ChatOptions? options)
        {
            options ??= new ChatOptions();

            double? temperature = customAssistant.Temperature.IsCustomValueSet ? customAssistant.Temperature.ActualValue : null;
            double? topP = customAssistant.TopP.IsCustomValueSet ? customAssistant.TopP.ActualValue : null;

            if (temperature is not null) options.Temperature = (float)temperature.Value;
            if (topP is not null) options.TopP = (float)topP.Value;
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }
    }
}