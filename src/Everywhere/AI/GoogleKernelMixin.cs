using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="IKernelMixin"/> for Google Gemini models.
/// </summary>
public sealed class GoogleKernelMixin : KernelMixinBase
{
    public override IChatCompletionService ChatCompletionService { get; }

    public GoogleKernelMixin(
        CustomAssistant customAssistant,
        HttpClient httpClient,
        ILoggerFactory loggerFactory
    ) : base(customAssistant)
    {
        httpClient.BaseAddress = new Uri(Endpoint, UriKind.Absolute);

        ChatCompletionService = new GoogleAIGeminiChatCompletionService(
            ModelId,
            ApiKey ?? string.Empty,
            ResolveApiVersion(Endpoint),
            httpClient,
            loggerFactory);
    }

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null)
    {
        double? temperature = _customAssistant.Temperature.IsCustomValueSet ? _customAssistant.Temperature.ActualValue : null;
        double? topP = _customAssistant.TopP.IsCustomValueSet ? _customAssistant.TopP.ActualValue : null;

        return new GeminiPromptExecutionSettings
        {
            Temperature = temperature,
            TopP = topP,
            FunctionChoiceBehavior = functionChoiceBehavior
        };
    }

    private static GoogleAIVersion ResolveApiVersion(string endpoint)
    {
        return endpoint.Contains("v1beta", StringComparison.OrdinalIgnoreCase)
            ? GoogleAIVersion.V1_Beta
            : GoogleAIVersion.V1;
    }
}
