using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Serialization;
using DynamicData;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Cloud;

public sealed partial class OfficialModelProvider : IOfficialModelProvider, IAsyncInitializer, IDisposable
{
    public ReadOnlyObservableCollection<ModelDefinitionTemplate> ModelDefinitions
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
    private readonly IKeyValueStorage _keyValueStorage;
    private readonly ILogger<OfficialModelProvider> _logger;

    private readonly SourceList<ModelDefinitionTemplate> _modelDefinitionsSource = new();
    private readonly Subject<Unit> _refreshRequestSubject = new();
    private readonly ReadOnlyObservableCollection<ModelDefinitionTemplate> _modelDefinitions;
    private readonly CompositeDisposable _disposables;

    // State tracking (Optional, avoids reloading if data is fresh)
    private DateTimeOffset _lastFetchTime = DateTimeOffset.MinValue;

    private const string StorageKey = "OfficialModelProvider.ModelDefinitions";

    public OfficialModelProvider(IHttpClientFactory httpClientFactory, IKeyValueStorage keyValueStorage, ILogger<OfficialModelProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _keyValueStorage = keyValueStorage;
        _logger = logger;

        // Load cached definitions from storage on startup (if available)
        if (keyValueStorage.Get<IReadOnlyList<ModelDefinitionTemplate>>(StorageKey) is { Count: > 0 } cachedDefinitions)
        {
            _modelDefinitionsSource.AddRange(cachedDefinitions);
        }

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
            .Select(_ => Observable.FromAsync(RefreshImplAsync))
            // 'Switch' subscribes to the NEW task and DISPOSES (Cancels) the previous one if it's running.
            .Switch()
            .Subscribe();

        _disposables = new CompositeDisposable(listSubscription, refreshSubscription, _refreshRequestSubject);
    }

    /// <summary>
    /// Actually performs the refresh by calling the official API, parsing the response, and updating the internal list and cache.
    /// </summary>
    /// <param name="cancellationToken"></param>
    private async Task RefreshImplAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (CloudConstants.AIGatewayBaseUrl.IsNullOrEmpty()) return;

            using var httpClient = _httpClientFactory.CreateClient(nameof(ICloudClient));

            var request = new HttpRequestMessage(HttpMethod.Get, $"{CloudConstants.AIGatewayBaseUrl}/v1/models");
            var response = await httpClient.SendAsync(request, cancellationToken);
            var payload = await ApiPayload<IReadOnlyList<CloudModelDefinition>>.EnsureSuccessFromHttpResponseJsonAsync(
                response,
                ModelsResponseJsonSerializerContext.Default.Options,
                cancellationToken);

            var cloudModelDefinitions = payload.EnsureData();
            var modelDefinitions = cloudModelDefinitions.AsValueEnumerable().Select(m => m.ToModelDefinitionTemplate()).ToList();

            _modelDefinitionsSource.Edit(innerList => innerList.Reset(modelDefinitions));
            _keyValueStorage.Set(StorageKey, modelDefinitions); // Update cache in storage

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

    /// <summary>
    /// Triggers a refresh of the model definitions from the official source and waits for it to complete.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {

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
        [property: JsonPropertyName("toolCall")] bool SupportsToolCall,
        [property: JsonPropertyName("knowledge")] string? KnowledgeCutoff,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
        [property: JsonPropertyName("deprecationDate")] string? DeprecationDate,
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
                KnowledgeCutoff = DateOnly.TryParse(KnowledgeCutoff, out var knowledgeDate) ? knowledgeDate : null,
                ReleaseDate = DateOnly.TryParse(ReleaseDate, out var releaseDate) ? releaseDate : null,
                DeprecationDate = DateOnly.TryParse(DeprecationDate, out var deprecationDate) ? deprecationDate : null,
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                ContextLimit = LimitInfo.Context,
                OutputLimit = LimitInfo.Output
            };

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) => modalityStrings.AsValueEnumerable().Aggregate(
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

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    public Task InitializeAsync()
    {
        _refreshRequestSubject.OnNext(Unit.Default); // Trigger an initial refresh on startup.
        return Task.CompletedTask;
    }

    #endregion

}