using Everywhere.Cloud;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public class OfficialKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public override string Endpoint => CloudConstants.AIGatewayBaseUrl;

    public override string ApiKey => "PLACEHOLDER";

    public OfficialKernelMixin(CustomAssistant customAssistant, HttpClient httpClient, ILoggerFactory loggerFactory) : base(customAssistant)
    {
        ChatCompletionService = ModelId.Split('/').FirstOrDefault()?.ToLowerInvariant() switch
        {
            "openai" => new OpenAIResponsesKernelMixin(customAssistant, httpClient, loggerFactory).ChatCompletionService,
            "google" => new GoogleKernelMixin(customAssistant, httpClient, loggerFactory).ChatCompletionService,
            "anthropic" => new AnthropicKernelMixin(customAssistant, httpClient).ChatCompletionService,
            "deepseek" => new DeepSeekKernelMixin(customAssistant, httpClient, loggerFactory).ChatCompletionService,
            _ => new OpenAIKernelMixin(customAssistant, httpClient, loggerFactory).ChatCompletionService
        };
    }
}