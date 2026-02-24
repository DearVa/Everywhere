using Everywhere.Cloud;
using Everywhere.Common;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

/// <summary>
/// A factory for creating instances of <see cref="KernelMixin"/>.
/// </summary>
public sealed class KernelMixinFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IKernelMixinFactory
{
    private readonly Lock _syncLock = new();

    private HttpClient? _cachedHttpClient;
    private KernelMixin? _cachedKernelMixin;

    /// <summary>
    /// Gets an existing <see cref="KernelMixin"/> instance from the cache or creates a new one.
    /// </summary>
    /// <param name="customAssistant">The custom assistant configuration to use for creating the kernel mixin.</param>
    /// <returns>A cached or new instance of <see cref="KernelMixin"/>.</returns>
    /// <exception cref="HandledChatException">Thrown if the model provider or definition is not found or not supported.</exception>
    public KernelMixin GetOrCreate(CustomAssistant customAssistant)
    {
        using var lockScope = _syncLock.EnterScope();

        if (customAssistant.ModelId.IsNullOrWhiteSpace())
        {
            throw new HandledChatException(
                new InvalidOperationException("Model ID cannot be empty."),
                HandledChatExceptionType.InvalidConfiguration);
        }

        var (connection, httpClient) = ResolveConnection(customAssistant);

        if (_cachedKernelMixin is not null &&
            _cachedKernelMixin.Connection == connection &&
            _cachedKernelMixin.ModelId == customAssistant.ModelId &&
            _cachedKernelMixin.RequestTimeoutSeconds == customAssistant.RequestTimeoutSeconds.ActualValue)
        {
            return _cachedKernelMixin;
        }

        _cachedHttpClient?.Dispose();
        _cachedKernelMixin?.Dispose();
        _cachedHttpClient = httpClient;

        return _cachedKernelMixin = connection.Schema switch
        {
            ModelProviderSchema.OpenAI => new OpenAIKernelMixin(customAssistant, connection, httpClient, loggerFactory),
            ModelProviderSchema.OpenAIResponses => new OpenAIResponsesKernelMixin(customAssistant, connection, httpClient, loggerFactory),
            ModelProviderSchema.Anthropic => new AnthropicKernelMixin(customAssistant, connection, httpClient),
            ModelProviderSchema.Google => new GoogleKernelMixin(customAssistant, connection, httpClient, loggerFactory),
            ModelProviderSchema.Ollama => new OllamaKernelMixin(customAssistant, connection, httpClient),
            _ => throw new HandledChatException(
                new NotSupportedException($"Model provider schema '{connection.Schema}' is not supported."),
                HandledChatExceptionType.InvalidConfiguration,
                new DynamicResourceKey(LocaleKey.KernelMixinFactory_UnsupportedModelProviderSchema))
        };
    }

    /// <summary>
    /// Resolves the <see cref="ModelConnection"/> and <see cref="HttpClient"/> for the given <see cref="CustomAssistant"/>.
    /// For Official mode, the schema is inferred from the ModelId prefix, endpoint comes from the AI gateway,
    /// and the API key is null (OAuth is handled by the named HttpClient).
    /// For user-configured modes, endpoint/apiKey are read from the assistant configuration.
    /// </summary>
    private (ModelConnection Connection, HttpClient HttpClient) ResolveConnection(CustomAssistant customAssistant)
    {
        return customAssistant.Schema is ModelProviderSchema.Official ?
            ResolveOfficialConnection(customAssistant) :
            ResolveUserConnection(customAssistant);
    }

    /// <summary>
    /// Resolves connection for Official (cloud gateway) mode.
    /// The actual provider schema is inferred from the model ID prefix (e.g. "openai/gpt-4o" → OpenAIResponses).
    /// </summary>
    private (ModelConnection Connection, HttpClient HttpClient) ResolveOfficialConnection(CustomAssistant customAssistant)
    {
        var schema = InferSchemaFromModelId(customAssistant.ModelId);

        var endpoint = schema.NormalizeEndpoint(CloudConstants.AIGatewayBaseUrl) ??
            throw new HandledChatException(
                new InvalidOperationException("AI Gateway base URL is not configured."),
                HandledChatExceptionType.InvalidEndpoint);

        // Official mode uses OAuth via the named HttpClient — no user API key needed.
        // Some SDKs require a non-null credential, so we pass null and let each mixin handle it
        // (e.g. OpenAIKernelMixin uses NoneAuthenticationPolicy, others use "official" placeholder).
        var connection = new ModelConnection(schema, endpoint, ApiKey: null);
        var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));

        return (connection, httpClient);
    }

    /// <summary>
    /// Resolves connection for user-configured (non-Official) modes.
    /// </summary>
    private (ModelConnection Connection, HttpClient HttpClient) ResolveUserConnection(CustomAssistant customAssistant)
    {
        if (!Uri.TryCreate(customAssistant.Endpoint, UriKind.Absolute, out _))
        {
            throw new HandledChatException(
                new InvalidOperationException("Invalid endpoint URL."),
                HandledChatExceptionType.InvalidEndpoint);
        }

        var endpoint = customAssistant.Schema.NormalizeEndpoint(customAssistant.Endpoint) ??
            throw new HandledChatException(
                new InvalidOperationException("Endpoint cannot be empty."),
                HandledChatExceptionType.InvalidEndpoint);

        var apiKey = Configuration.ApiKey.GetKey(customAssistant.ApiKey);
        var connection = new ModelConnection(customAssistant.Schema, endpoint, apiKey);

        // Create an HttpClient instance using the factory.
        // It will have the configured settings (timeout and proxy).
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(customAssistant.RequestTimeoutSeconds.ActualValue);

        return (connection, httpClient);
    }

    /// <summary>
    /// Infers the actual <see cref="ModelProviderSchema"/> from an Official model ID.
    /// Official model IDs follow the format "provider/model-name" (e.g. "openai/gpt-4o", "google/gemini-2.5-flash").
    /// </summary>
    private static ModelProviderSchema InferSchemaFromModelId(string? modelId)
    {
        var provider = modelId?.Split('/').FirstOrDefault()?.ToLowerInvariant();
        return provider switch
        {
            "openai" => ModelProviderSchema.OpenAIResponses,
            "google" => ModelProviderSchema.Google,
            "anthropic" => ModelProviderSchema.Anthropic,
            _ => ModelProviderSchema.OpenAI
        };
    }
}