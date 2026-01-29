// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Data;

namespace Microsoft.SemanticKernel.Plugins.Web.Google;

/// <summary>
/// A Google Text Search implementation that can be used to perform searches using the Google Web Search API.
/// </summary>
public sealed partial class GoogleTextSearch : ITextSearch, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleTextSearch"/> class.
    /// </summary>
    /// <param name="searchEngineId">Google Search Engine ID (looks like "a12b345...")</param>
    /// <param name="apiKey">Google Custom Search API (looks like "ABcdEfG1...")</param>
    /// <param name="options">Options used when creating this instance of <see cref="GoogleTextSearch"/>.</param>
    public GoogleTextSearch(
        string searchEngineId,
        string apiKey,
        GoogleTextSearchOptions? options = null)
        : this(searchEngineId, apiKey, httpClient: null, baseUri: null, options)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleTextSearch"/> class.
    /// </summary>
    /// <param name="searchEngineId">Google Search Engine ID (looks like "a12b345...")</param>
    /// <param name="apiKey">Google Custom Search API key</param>
    /// <param name="httpClient">The HTTP client to use for requests. If null, a new client will be created.</param>
    /// <param name="baseUri">The base URI for the Google Custom Search API. If null, defaults to https://customsearch.googleapis.com</param>
    /// <param name="options">Options used when creating this instance of <see cref="GoogleTextSearch"/>.</param>
    public GoogleTextSearch(
        string searchEngineId,
        string apiKey,
        HttpClient? httpClient,
        Uri? baseUri = null,
        GoogleTextSearchOptions? options = null)
    {
        Verify.NotNullOrWhiteSpace(apiKey);
        Verify.NotNullOrWhiteSpace(searchEngineId);

        this._apiKey = apiKey;
        this._searchEngineId = searchEngineId;
        this._baseUri = baseUri ?? new Uri("https://customsearch.googleapis.com");
        this._logger = options?.LoggerFactory?.CreateLogger(typeof(GoogleTextSearch)) ?? NullLogger.Instance;
        this._stringMapper = options?.StringMapper ?? s_defaultStringMapper;
        this._resultMapper = options?.ResultMapper ?? s_defaultResultMapper;

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
    public async Task<KernelSearchResults<object>> GetSearchResultsAsync(string query, TextSearchOptions? searchOptions = null, CancellationToken cancellationToken = default)
    {
        searchOptions ??= new TextSearchOptions();
        var searchResponse = await this.ExecuteSearchAsync(query, searchOptions, cancellationToken).ConfigureAwait(false);

        long? totalCount = searchOptions.IncludeTotalCount && searchResponse?.SearchInformation?.TotalResults != null
            ? long.Parse(searchResponse.SearchInformation.TotalResults)
            : null;

        return new KernelSearchResults<object>(this.GetResultsAsResultAsync(searchResponse, cancellationToken), totalCount, GetResultsMetadata(searchResponse));
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<TextSearchResult>> GetTextSearchResultsAsync(string query, TextSearchOptions? searchOptions = null, CancellationToken cancellationToken = default)
    {
        searchOptions ??= new TextSearchOptions();
        var searchResponse = await this.ExecuteSearchAsync(query, searchOptions, cancellationToken).ConfigureAwait(false);

        long? totalCount = searchOptions.IncludeTotalCount && searchResponse?.SearchInformation?.TotalResults != null
            ? long.Parse(searchResponse.SearchInformation.TotalResults)
            : null;

        return new KernelSearchResults<TextSearchResult>(this.GetResultsAsTextSearchResultAsync(searchResponse, cancellationToken), totalCount, GetResultsMetadata(searchResponse));
    }

    /// <inheritdoc/>
    public async Task<KernelSearchResults<string>> SearchAsync(string query, TextSearchOptions? searchOptions = null, CancellationToken cancellationToken = default)
    {
        searchOptions ??= new TextSearchOptions();
        var searchResponse = await this.ExecuteSearchAsync(query, searchOptions, cancellationToken).ConfigureAwait(false);

        long? totalCount = searchOptions.IncludeTotalCount && searchResponse?.SearchInformation?.TotalResults != null
            ? long.Parse(searchResponse.SearchInformation.TotalResults)
            : null;

        return new KernelSearchResults<string>(this.GetResultsAsStringAsync(searchResponse, cancellationToken), totalCount, GetResultsMetadata(searchResponse));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this._disposeHttpClient)
        {
            this._httpClient.Dispose();
        }
    }

    #region private

    private const int MaxCount = 10;

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _searchEngineId;
    private readonly Uri _baseUri;
    private readonly bool _disposeHttpClient;
    private readonly ITextSearchStringMapper _stringMapper;
    private readonly ITextSearchResultMapper _resultMapper;

    private static readonly ITextSearchStringMapper s_defaultStringMapper = new DefaultTextSearchStringMapper();
    private static readonly ITextSearchResultMapper s_defaultResultMapper = new DefaultTextSearchResultMapper();

    // See https://developers.google.com/custom-search/v1/reference/rest/v1/cse/list
    private static readonly string[] s_queryParameters = ["cr", "dateRestrict", "exactTerms", "excludeTerms", "filter", "gl", "hl", "linkSite", "lr", "orTerms", "rights", "siteSearch", "siteSearchFilter"];

    /// <summary>
    /// Execute a Google search
    /// </summary>
    /// <param name="query">The query string.</param>
    /// <param name="searchOptions">Search options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the request.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    private async Task<GoogleSearchResponse?> ExecuteSearchAsync(string query, TextSearchOptions searchOptions, CancellationToken cancellationToken)
    {
        var count = searchOptions.Top;
        var offset = searchOptions.Skip;

        if (count is <= 0 or > MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(searchOptions), count, $"{nameof(searchOptions)}.Count value must be greater than 0 and less than or equals 10.");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(searchOptions), offset, $"{nameof(searchOptions)}.Offset value must be greater than 0.");
        }

        // Build the request URL with base parameters
        var queryString = $"key={HttpUtility.UrlEncode(this._apiKey)}" +
                          $"&cx={HttpUtility.UrlEncode(this._searchEngineId)}" +
                          $"&q={HttpUtility.UrlEncode(query)}" +
                          $"&num={count}" +
                          (offset > 0 ? $"&start={offset}" : string.Empty);

        // Add filter parameters
        queryString += this.BuildFilterQueryString(searchOptions);

        var requestUri = new UriBuilder(this._baseUri)
        {
            Path = "/customsearch/v1",
            Query = queryString
        }.Uri;

        this._logger.LogDebug("Sending Google Custom Search request");

        using var response = await this._httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            this._logger.LogError("Google Custom Search API returned error: {StatusCode} - {Content}", response.StatusCode, errorContent);
            throw new HttpRequestException($"Google Custom Search API returned error: {response.StatusCode} - {errorContent}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, GoogleTextSearchJsonContext.Default.GoogleSearchResponse);
    }

#pragma warning disable CS0618 // FilterClause is obsolete
    /// <summary>
    /// Build query string parameters from filter options.
    /// </summary>
    /// <param name="searchOptions">Text search options.</param>
    /// <returns>Query string with filter parameters.</returns>
    private string BuildFilterQueryString(TextSearchOptions searchOptions)
    {
        if (searchOptions.Filter is null)
        {
            return string.Empty;
        }

        var filterClauses = searchOptions.Filter.FilterClauses;
        var queryBuilder = new StringBuilder();

        foreach (var filterClause in filterClauses)
        {
            if (filterClause is EqualToFilterClause equalityFilterClause)
            {
                if (equalityFilterClause.Value is not string value)
                {
                    continue;
                }

                var fieldNameUpper = equalityFilterClause.FieldName.ToUpperInvariant();
                var matchedParam = Array.Find(s_queryParameters, p => p.Equals(equalityFilterClause.FieldName, StringComparison.OrdinalIgnoreCase));

                if (matchedParam is not null)
                {
                    queryBuilder.Append($"&{matchedParam}={HttpUtility.UrlEncode(value)}");

                    // For siteSearch, also add siteSearchFilter=i to include results from that site
                    if (fieldNameUpper == "SITESEARCH")
                    {
                        queryBuilder.Append("&siteSearchFilter=i");
                    }
                }
                else
                {
                    throw new ArgumentException($"Unknown equality filter clause field name '{equalityFilterClause.FieldName}', must be one of {string.Join(",", s_queryParameters)}", nameof(searchOptions));
                }
            }
        }

        return queryBuilder.ToString();
    }
#pragma warning restore CS0618 // FilterClause is obsolete

    /// <summary>
    /// Return the search results as instances of <see cref="TextSearchResult"/>.
    /// </summary>
    private async IAsyncEnumerable<TextSearchResult> GetResultsAsTextSearchResultAsync(GoogleSearchResponse? searchResponse, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (searchResponse?.Items is null)
        {
            yield break;
        }

        foreach (var item in searchResponse.Items)
        {
            yield return this._resultMapper.MapFromResultToTextSearchResult(item);
            await Task.Yield();
        }
    }

    /// <summary>
    /// Return the search results as instances of <see cref="string"/>.
    /// </summary>
    private async IAsyncEnumerable<string> GetResultsAsStringAsync(GoogleSearchResponse? searchResponse, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (searchResponse?.Items is null)
        {
            yield break;
        }

        foreach (var item in searchResponse.Items)
        {
            yield return this._stringMapper.MapFromResultToString(item);
            await Task.Yield();
        }
    }

    /// <summary>
    /// Return the search results as instances of <see cref="GoogleSearchItem"/>.
    /// </summary>
    private async IAsyncEnumerable<GoogleSearchItem> GetResultsAsResultAsync(GoogleSearchResponse? searchResponse, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (searchResponse?.Items is null)
        {
            yield break;
        }

        foreach (var item in searchResponse.Items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    /// <summary>
    /// Return the results metadata.
    /// </summary>
    private static Dictionary<string, object?>? GetResultsMetadata(GoogleSearchResponse? searchResponse)
    {
        return new Dictionary<string, object?>()
        {
            { "SearchTime", searchResponse?.SearchInformation?.SearchTime },
            { "TotalResults", searchResponse?.SearchInformation?.TotalResults },
        };
    }

    /// <summary>
    /// Default implementation which maps from a <see cref="GoogleSearchItem"/> to a <see cref="string"/>
    /// </summary>
    private sealed class DefaultTextSearchStringMapper : ITextSearchStringMapper
    {
        /// <inheritdoc />
        public string MapFromResultToString(object result)
        {
            if (result is not GoogleSearchItem googleResult)
            {
                throw new ArgumentException("Result must be a GoogleSearchItem", nameof(result));
            }

            return googleResult.Snippet ?? string.Empty;
        }
    }

    /// <summary>
    /// Default implementation which maps from a <see cref="GoogleSearchItem"/> to a <see cref="TextSearchResult"/>
    /// </summary>
    private sealed class DefaultTextSearchResultMapper : ITextSearchResultMapper
    {
        /// <inheritdoc />
        public TextSearchResult MapFromResultToTextSearchResult(object result)
        {
            if (result is not GoogleSearchItem googleResult)
            {
                throw new ArgumentException("Result must be a GoogleSearchItem", nameof(result));
            }

            return new TextSearchResult(googleResult.Snippet ?? string.Empty) { Name = googleResult.Title, Link = googleResult.Link };
        }
    }

    #endregion

    #region Response Models

    /// <summary>
    /// Google Custom Search API response
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    public sealed class GoogleSearchResponse
    {
        /// <summary>
        /// Search result items
        /// </summary>
        [JsonPropertyName("items")]
        public IReadOnlyList<GoogleSearchItem>? Items { get; set; }

        /// <summary>
        /// Search information
        /// </summary>
        [JsonPropertyName("searchInformation")]
        public GoogleSearchInformation? SearchInformation { get; set; }
    }

    /// <summary>
    /// Google Custom Search result item
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    public sealed class GoogleSearchItem
    {
        /// <summary>
        /// Title of the search result
        /// </summary>
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>
        /// URL of the search result
        /// </summary>
        [JsonPropertyName("link")]
        public string? Link { get; set; }

        /// <summary>
        /// Snippet/description of the search result
        /// </summary>
        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }

        /// <summary>
        /// Display URL
        /// </summary>
        [JsonPropertyName("displayLink")]
        public string? DisplayLink { get; set; }
    }

    /// <summary>
    /// Google search information
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Used by JSON deserialization")]
    public sealed class GoogleSearchInformation
    {
        /// <summary>
        /// Total number of results
        /// </summary>
        [JsonPropertyName("totalResults")]
        public string? TotalResults { get; set; }

        /// <summary>
        /// Time taken for the search
        /// </summary>
        [JsonPropertyName("searchTime")]
        public double SearchTime { get; set; }
    }

    [JsonSerializable(typeof(GoogleSearchResponse))]
    private sealed partial class GoogleTextSearchJsonContext : JsonSerializerContext;

    #endregion
}
