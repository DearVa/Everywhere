using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Serialization;
using DynamicData;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

public sealed partial class OfficialModelProvider : IOfficialModelProvider, IAsyncInitializer, IDisposable
{
    public IReadOnlyList<ModelDefinitionTemplate> ModelDefinitions
    {
        get
        {
            // Push a signal to request a refresh.
            // This is non-blocking and extremely cheap.
            _refreshRequestSubject.OnNext(Unit.Default);
            return _modelDefinitions;
        }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OfficialModelProvider> _logger;

    private readonly SourceList<ModelDefinitionTemplate> _modelDefinitionsSource = new();
    private readonly Subject<Unit> _refreshRequestSubject = new();
    private readonly ReadOnlyObservableCollection<ModelDefinitionTemplate> _modelDefinitions;
    private readonly CompositeDisposable _disposables;

    // State tracking (Optional, avoids reloading if data is fresh)
    private DateTimeOffset _lastFetchTime = DateTimeOffset.MinValue;

    public OfficialModelProvider(IHttpClientFactory httpClientFactory, ILogger<OfficialModelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Bind SourceList to ObservableCollection (Standard DynamicData pattern)
        var listSubscription = _modelDefinitionsSource.Connect()
            .Bind(out _modelDefinitions)
            .Subscribe();

        // 2. The Refresher Pipeline
        //    This separates the "Trigger" (Getter) from the "Execution" (HTTP)
        var refreshSubscription = _refreshRequestSubject
            .Throttle(TimeSpan.FromMilliseconds(300))
            .Where(_ => DateTimeOffset.Now - _lastFetchTime > TimeSpan.FromSeconds(10))
            // 'Select' projects the signal into an Async Task.
            .Select(_ => Observable.FromAsync(RefreshModelDefinitionsAsync))
            // 'Switch' subscribes to the NEW task and DISPOSES (Cancels) the previous one if it's running.
            .Switch()
            .Subscribe();

        _disposables = new CompositeDisposable(listSubscription, refreshSubscription, _refreshRequestSubject);
    }

    public async Task RefreshModelDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (CloudConstants.AIGatewayBaseUrl.IsNullOrEmpty()) return;

            using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));

            var request = new HttpRequestMessage(HttpMethod.Get, $"{CloudConstants.AIGatewayBaseUrl}/models");
            var response = await httpClient.SendAsync(request, cancellationToken);
            var payload = await ApiPayload<IReadOnlyList<CloudModelDefinition>>.EnsureSuccessFromHttpResponseJsonAsync(
                response,
                ModelsResponseJsonSerializerContext.Default.Options,
                cancellationToken);
            var cloudModelDefinitions = payload.EnsureData();

            _modelDefinitionsSource.Edit(innerList => innerList.Reset(cloudModelDefinitions.Select(c => c.ToModelDefinitionTemplate())));
            _lastFetchTime = DateTimeOffset.Now;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing model definitions");

            // Avoid hammering the endpoint on failure, but allow retries sooner than the normal 10s.
            _lastFetchTime = DateTimeOffset.Now - TimeSpan.FromSeconds(7);
        }
    }

    public void Dispose() => _disposables.Dispose();

    /// <summary>
    /// Standard model definition according to https://models.dev/api.json
    /// </summary>
    /// <param name="ModelId"></param>
    /// <param name="Name"></param>
    /// <param name="Attachment"></param>
    /// <param name="SupportsReasoning"></param>
    /// <param name="SupportsToolCall"></param>
    /// <param name="Modalities"></param>
    /// <param name="LimitInfo"></param>
    private sealed record CloudModelDefinition(
        [property: JsonPropertyName("id")] string ModelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("attachment")] bool Attachment,
        [property: JsonPropertyName("reasoning")] bool SupportsReasoning,
        [property: JsonPropertyName("tool_call")] bool SupportsToolCall,
        [property: JsonPropertyName("modalities")] CloudModelModalities Modalities,
        [property: JsonPropertyName("limit")] CloudModelLimitInfo LimitInfo
    )
    {
        public ModelDefinitionTemplate ToModelDefinitionTemplate() =>
            new()
            {
                ModelId = ModelId,
                Name = Name,
                SupportsReasoning = SupportsReasoning,
                SupportsToolCall = SupportsToolCall,
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                ContextLimit = LimitInfo.Context
            };

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) => modalityStrings.Aggregate(
            AI.Modalities.None,
            (current, modality) => current | modality.ToLower() switch
            {
                "text" => AI.Modalities.Text,
                "image" => AI.Modalities.Image,
                "audio" => AI.Modalities.Audio,
                "video" => AI.Modalities.Video,
                "pdf" => AI.Modalities.Pdf,
                _ => AI.Modalities.None
            });
    }

    private sealed record CloudModelModalities(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("output")] IReadOnlyList<string> Output
    );

    private sealed record CloudModelLimitInfo(
        [property: JsonPropertyName("context")] int Context,
        [property: JsonPropertyName("input")] int Input = 0,
        [property: JsonPropertyName("output")] int Output = 0
    );

    [JsonSerializable(typeof(ApiPayload<IReadOnlyList<CloudModelDefinition>>))]
    private sealed partial class ModelsResponseJsonSerializerContext : JsonSerializerContext;

    #region Async Initializer Implementation

    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Startup;

    public Task InitializeAsync()
    {
        _refreshRequestSubject.OnNext(Unit.Default); // Trigger an initial refresh on startup.
        return Task.CompletedTask;
    }

    #endregion

}