// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.SemanticKernel.Plugins.Web.Google;

/// <summary>
/// Google search connector.
/// Provides methods to search using Google Custom Search API.
/// </summary>
public sealed partial class GoogleConnector : IWebSearchEngineConnector, IDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _searchEngineId;
    private readonly Uri _baseUri;
    private readonly bool _disposeHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleConnector"/> class.
    /// </summary>
    /// <param name="apiKey">Google Custom Search API (looks like "ABcdEfG1...")</param>
    /// <param name="searchEngineId">Google Search Engine ID (looks like "a12b345...")</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
    public GoogleConnector(
        string apiKey,
        string searchEngineId,
        ILoggerFactory? loggerFactory = null)
        : this(apiKey, searchEngineId, httpClient: null, baseUri: null, loggerFactory)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleConnector"/> class.
    /// </summary>
    /// <param name="apiKey">Google Custom Search API key</param>
    /// <param name="searchEngineId">Google Search Engine ID (looks like "a12b345...")</param>
    /// <param name="httpClient">The HTTP client to use for requests. If null, a new client will be created.</param>
    /// <param name="baseUri">The base URI for the Google Custom Search API. If null, defaults to https://customsearch.googleapis.com</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
    public GoogleConnector(
        string apiKey,
        string searchEngineId,
        HttpClient? httpClient,
        Uri? baseUri = null,
        ILoggerFactory? loggerFactory = null)
    {
        Verify.NotNullOrWhiteSpace(apiKey);
        Verify.NotNullOrWhiteSpace(searchEngineId);

        this._apiKey = apiKey;
        this._searchEngineId = searchEngineId;
        this._baseUri = baseUri ?? new Uri("https://customsearch.googleapis.com");
        this._logger = loggerFactory?.CreateLogger(typeof(GoogleConnector)) ?? NullLogger.Instance;

        if (httpClient is null)
        {
            this._httpClient = new HttpClient();
            this._disposeHttpClient = true;
        }
        else
        {
            this._httpClient = httpClient;
            this._disposeHttpClient = false;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<T>> SearchAsync<T>(
        string query,
        int count,
        int offset,
        CancellationToken cancellationToken)
    {
        if (count is <= 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than or equals 10.");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        // Build the request URL
        var requestUri = new UriBuilder(this._baseUri)
        {
            Path = "/customsearch/v1",
            Query = $"key={HttpUtility.UrlEncode(this._apiKey)}" +
                    $"&cx={HttpUtility.UrlEncode(this._searchEngineId)}" +
                    $"&q={HttpUtility.UrlEncode(query)}" +
                    $"&num={count}" +
                    (offset > 0 ? $"&start={offset}" : string.Empty)
        }.Uri;

        this._logger.LogDebug("Sending Google Custom Search request: {Uri}", requestUri);

        using var response = await this._httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this._logger.LogError("Google Custom Search API returned error: {StatusCode} - {Content}", response.StatusCode, errorContent);
            throw new HttpRequestException($"Google Custom Search API returned error: {response.StatusCode} - {errorContent}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        this._logger.LogDebug("Received response from Google Custom Search API");

        var searchResponse = JsonSerializer.Deserialize(json, GoogleConnectorJsonContext.Default.GoogleSearchResponse);

        List<T>? returnValues = null;
        if (searchResponse?.Items is not null)
        {
            if (typeof(T) == typeof(string))
            {
                returnValues = searchResponse.Items.Select(item => item.Snippet ?? string.Empty).ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                List<WebPage> webPages = [];
                foreach (var item in searchResponse.Items)
                {
                    WebPage webPage = new()
                    {
                        Name = item.Title ?? string.Empty,
                        Snippet = item.Snippet ?? string.Empty,
                        Url = item.Link ?? string.Empty
                    };
                    webPages.Add(webPage);
                }
                returnValues = webPages.Take(count).ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }
        }

        return
            returnValues is null ? [] :
            returnValues.Count <= count ? returnValues :
            returnValues.Take(count);
    }

    /// <summary>
    /// Disposes the <see cref="GoogleConnector"/> instance.
    /// </summary>
    public void Dispose()
    {
        if (this._disposeHttpClient)
        {
            this._httpClient.Dispose();
        }
    }

    #region Response Models

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    private sealed class GoogleSearchResponse
    {
        [JsonPropertyName("items")]
        public GoogleSearchItem[]? Items { get; set; }

        [JsonPropertyName("searchInformation")]
        public GoogleSearchInformation? SearchInformation { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    private sealed class GoogleSearchItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }

        [JsonPropertyName("displayLink")]
        public string? DisplayLink { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    private sealed class GoogleSearchInformation
    {
        [JsonPropertyName("totalResults")]
        public string? TotalResults { get; set; }

        [JsonPropertyName("searchTime")]
        public double SearchTime { get; set; }
    }

    [JsonSerializable(typeof(GoogleSearchResponse))]
    private sealed partial class GoogleConnectorJsonContext : JsonSerializerContext;

    #endregion
}
