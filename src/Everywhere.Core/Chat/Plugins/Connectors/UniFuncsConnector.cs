using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel.Plugins.Web;
using ZLinq;

namespace Everywhere.Chat.Plugins;

public partial class WebBrowserPlugin
{
    private partial class UniFuncsConnector : IWebSearchEngineConnector
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly Uri _uri;

        /// <summary>
        /// Initializes a new instance of the <see cref="UniFuncsConnector"/> class.
        /// </summary>
        /// <param name="apiKey">The API key to authenticate the connector.</param>
        /// <param name="httpClient"></param>
        /// <param name="uri">The URI of the UniFuncs Search instance. Defaults to "https://api.unifuncs.com/api/web-search/search".</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
        public UniFuncsConnector(string apiKey, HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory)
        {
            _httpClient = httpClient;
            _logger = loggerFactory?.CreateLogger(typeof(UniFuncsConnector)) ?? NullLogger.Instance;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _uri = uri;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 1, CancellationToken cancellationToken = default)
        {
            if (count is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 1 and less than 50.");
            }

            // offset is default = 0 when calling for compatibility, but UniFuncs API expects minimum 1
            offset = offset < 1 ? 1 : offset;

            _logger.LogDebug("Sending request: {Uri}", _uri);

            using var responseMessage = await _httpClient.PostAsync(
                _uri,
                JsonContent.Create(
                    new
                    {
                        query,
                        page = offset,
                        count,
                    }),
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);

            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, UniFuncsResponseJsonSerializationContext.Default.UniFuncsResponse);
            if (response is not { Code: 0, Data.WebPages: { } webPages})
            {
                throw new HttpRequestException($"UniFuncs API returned error: {response?.Code}");
            }

            if (webPages is null || webPages.Length == 0) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = webPages
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Summary ?? x.Snippet)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = webPages
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Name,
                        Url = x.Url,
                        Snippet = x.Snippet
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        [JsonSerializable(typeof(UniFuncsResponse))]
        private partial class UniFuncsResponseJsonSerializationContext : JsonSerializerContext;

        private class UniFuncsResponse
        {
            [JsonPropertyName("code")]
            public int Code { get; init; }

            [JsonPropertyName("message")]
            public string? Message { get; init; }

            [JsonPropertyName("data")]
            public UniFuncsSearchResult? Data { get; init; }
        }

        private sealed class UniFuncsSearchResult
        {
            [JsonPropertyName("webPages")]
            public UniFuncsWebPage[]? WebPages { get; init; }
        }

        private sealed class UniFuncsWebPage
        {
            /// <summary>
            /// The name/title of the web page.
            /// </summary>
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            /// <summary>
            /// The snippet of the web page.
            /// </summary>
            [JsonPropertyName("snippet")]
            public string Snippet { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the web page.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The summary of the web page.
            /// </summary>
            [JsonPropertyName("summary")]
            public string? Summary { get; set; }

            /// <summary>
            /// The date published of the web page.
            /// </summary>
            [JsonPropertyName("datePublished")]
            public string? DatePublished { get; set; }
        }
    }
}