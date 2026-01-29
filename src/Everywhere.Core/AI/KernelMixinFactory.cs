using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

/// <summary>
/// A factory for creating instances of <see cref="IKernelMixin"/>.
/// </summary>
public class KernelMixinFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IKernelMixinFactory
{
    private readonly Lock _syncLock = new();

    private KernelMixinBase? _cachedKernelMixin;

    /// <summary>
    /// Gets an existing <see cref="IKernelMixin"/> instance from the cache or creates a new one.
    /// </summary>
    /// <param name="customAssistant">The custom assistant configuration to use for creating the kernel mixin.</param>
    /// <param name="apiKeyOverride">An optional API key to override the one in the settings.</param>
    /// <returns>A cached or new instance of <see cref="IKernelMixin"/>.</returns>
    /// <exception cref="HandledChatException">Thrown if the model provider or definition is not found or not supported.</exception>
    public IKernelMixin GetOrCreate(CustomAssistant customAssistant, string? apiKeyOverride = null)
    {
        using var lockScope = _syncLock.EnterScope();

        if (!Uri.TryCreate(customAssistant.Endpoint, UriKind.Absolute, out _))
        {
            throw new HandledChatException(
                new InvalidOperationException("Invalid endpoint URL."),
                HandledChatExceptionType.InvalidEndpoint);
        }

        if (customAssistant.ModelId.IsNullOrWhiteSpace())
        {
            throw new HandledChatException(
                new InvalidOperationException("Model ID cannot be empty."),
                HandledChatExceptionType.InvalidConfiguration);
        }

        var apiKey = apiKeyOverride ?? ApiKey.GetKey(customAssistant.ApiKey);
        if (_cachedKernelMixin is not null &&
            _cachedKernelMixin.Schema == customAssistant.Schema &&
            _cachedKernelMixin.ModelId == customAssistant.ModelId &&
            _cachedKernelMixin.Endpoint == customAssistant.Endpoint?.Trim().Trim('/') &&
            _cachedKernelMixin.ApiKey == apiKey &&
            _cachedKernelMixin.RequestTimeoutSeconds == customAssistant.RequestTimeoutSeconds.ActualValue)
        {
            return _cachedKernelMixin;
        }

        _cachedKernelMixin?.Dispose();

        // Create an HttpClient instance using the factory.
        // It will have the configured settings (timeout and proxy).
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(customAssistant.RequestTimeoutSeconds.ActualValue);
        return _cachedKernelMixin = customAssistant.Schema switch
        {
            ModelProviderSchema.OpenAI => new OpenAIKernelMixin(customAssistant, httpClient, loggerFactory),
            ModelProviderSchema.OpenAIResponses => new OpenAIResponsesKernelMixin(customAssistant, httpClient, loggerFactory),
            ModelProviderSchema.Anthropic => new AnthropicKernelMixin(customAssistant, httpClient),
            ModelProviderSchema.Google => new GoogleKernelMixin(customAssistant, httpClient, loggerFactory),
            ModelProviderSchema.Ollama => new OllamaKernelMixin(customAssistant, httpClient),
            ModelProviderSchema.DeepSeek => new DeepSeekKernelMixin(customAssistant, httpClient, loggerFactory),
            _ => throw new HandledChatException(
                new NotSupportedException($"Model provider schema '{customAssistant.Schema}' is not supported."),
                HandledChatExceptionType.InvalidConfiguration,
                new DynamicResourceKey(LocaleKey.KernelMixinFactory_UnsupportedModelProviderSchema))
        };
    }
}