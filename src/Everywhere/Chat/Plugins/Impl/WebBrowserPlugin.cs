using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Brave;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using PuppeteerSharp;
using Tavily;
using ZLinq;
using IHttpClientFactory = System.Net.Http.IHttpClientFactory;

namespace Everywhere.Chat.Plugins;

public partial class WebBrowserPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_Description);
    public override LucideIconKind? Icon => LucideIconKind.Globe;

    public override IReadOnlyList<SettingsItem> SettingsItems => _webSearchEngineSettings.SettingsItems;

    private readonly WebSearchEngineSettings _webSearchEngineSettings;
    private readonly IRuntimeConstantProvider _runtimeConstantProvider;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IWebProxy _webProxy;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebBrowserPlugin> _logger;
    private readonly DebounceExecutor<WebBrowserPlugin, ThreadingTimerImpl> _browserDisposer;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = GeneratedJsonSerializationContext.Default
    };

    private readonly SemaphoreSlim _browserLock = new(1, 1);

    private IWebSearchEngineConnector? _connector;
    private int _maxSearchCount;
    private IBrowser? _browser;
    private Process? _browserProcess;

    public WebBrowserPlugin(
        Settings settings,
        IRuntimeConstantProvider runtimeConstantProvider,
        IWatchdogManager watchdogManager,
        IWebProxy webProxy,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory) : base("web_browser")
    {
        _webSearchEngineSettings = settings.Plugin.WebSearchEngine;
        _runtimeConstantProvider = runtimeConstantProvider;
        _watchdogManager = watchdogManager;
        _webProxy = webProxy;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WebBrowserPlugin>();
        _browserDisposer = new DebounceExecutor<WebBrowserPlugin, ThreadingTimerImpl>(
            () => this,
            static that =>
            {
                that._browserLock.Wait();
                try
                {
                    that._logger.LogDebug("Disposing browser after inactivity.");

                    if (that._browser is null) return;
                    that._browser.CloseAsync();
                    DisposeCollector.DisposeToDefault(ref that._browser);

                    if (that._browserProcess is { HasExited: false })
                    {
                        that._watchdogManager.UnregisterProcessAsync(that._browserProcess.Id);

                        // Kill existing browser process if any
                        that._browserProcess.Kill();
                        that._browserProcess = null;
                    }
                }
                finally
                {
                    that._browserLock.Release();
                }
            },
            TimeSpan.FromMinutes(5)); // Dispose browser after 5 minutes of inactivity

        _functionsSource.Edit(list =>
        {
            list.Add(
                new NativeChatFunction(
                    WebSearchAsync,
                    ChatFunctionPermissions.NetworkAccess));
            list.Add(
                new NativeChatFunction(
                    WebSnapshotAsync,
                    ChatFunctionPermissions.NetworkAccess));
        });

        new ObjectObserver(HandleSettingsChanged).Observe(_webSearchEngineSettings);
    }

    private void HandleSettingsChanged(in ObjectObserverChangedEventArgs e)
    {
        // Invalidate the connector when settings change
        _connector = null;
    }

    [MemberNotNull(nameof(_connector))]
    private void EnsureConnector()
    {
        if (_connector is not null) return;

        _logger.LogDebug("Ensuring web search engine connector is initialized.");

        if (_webSearchEngineSettings.SelectedWebSearchEngineProvider is not { } provider)
        {
            throw new HandledException(
                new ArgumentException("Web search engine provider is not selected."),
                new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_NoWebSearchEngineProviderSelected_ErrorMessage),
                showDetails: false);
        }

        if (!Uri.TryCreate(provider.EndPoint.ActualValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new HandledException(
                new ArgumentException("Endpoint is not a valid absolute URI."),
                new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_InvalidWebSearchEngineEndpoint_ErrorMessage),
                showDetails: false);
        }

        // Extract only the base URI without query parameters
        uri = new UriBuilder(uri) { Query = string.Empty }.Uri;

        (_connector, _maxSearchCount) = provider.Id.ToLower() switch
        {
            "google" => (new GoogleConnector(
                EnsureApiKey(provider.ApiKey),
                provider.SearchEngineId ??
                throw new HandledException(
                    new UnauthorizedAccessException("Search Engine ID is not set."),
                    new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_GoogleSearchEngineIdNotSet_ErrorMessage),
                    showDetails: false),
                _httpClientFactory.CreateClient(),
                uri,
                _loggerFactory) as IWebSearchEngineConnector, 10),
            "tavily" => (new TavilyConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), uri, _loggerFactory), 20),
            "brave" => (new BraveConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), new Uri(uri, "?q"), _loggerFactory), 20),
            "bocha" => (new BoChaConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), uri, _loggerFactory), 50),
            "jina" => (new JinaConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), new Uri(uri, "?q"), _loggerFactory), 50),
            "searxng" => (new SearxngConnector(_httpClientFactory.CreateClient(), uri, _loggerFactory), 50),
            _ => throw new HandledException(
                new NotSupportedException($"Web search engine provider '{provider.Id}' is not supported."),
                new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_UnsupportedWebSearchEngineProvider_ErrorMessage),
                showDetails: false)
        };

        string EnsureApiKey(string? apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ?
                throw new HandledException(
                    new UnauthorizedAccessException("API key is not set."),
                    new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_WebSearchEngineApiKeyNotSet_ErrorMessage),
                    showDetails: false) :
                apiKey;
    }

    /// <summary>
    /// Performs a web search using the provided query, count, and offset.
    /// </summary>
    /// <param name="userInterface"></param>
    /// <param name="query">The text to search for.</param>
    /// <param name="count">The number of results to return. Default is 10.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The value of the TResult parameter contains the search results as a string.</returns>
    /// <remarks>
    /// This method is marked as "unsafe." The usage of JavaScriptEncoder.UnsafeRelaxedJsonEscaping may introduce security risks.
    /// Only use this method if you are aware of the potential risks and have validated the input to prevent security vulnerabilities.
    /// </remarks>
    [KernelFunction("web_search")]
    [Description(
        "Perform a web search and return the results as a json array of web pages. " +
        "You can use the results to answer user questions with up-to-date information.")] // TODO: index (chat scope)
    [DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_WebSearch_Header, LocaleKey.NativeChatPlugin_WebBrowser_WebSearch_Description)]
    private async Task<string> WebSearchAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("Search query")] string query,
        [Description("Number of results")] int count = 10,
        CancellationToken cancellationToken = default) // TODO: Offset is not well supported.
    {
        _logger.LogDebug("Performing web search with query: {Query}, count: {Count}", query, count);

        userInterface.DisplaySink.AppendDynamicResourceKey(
            new FormattedDynamicResourceKey(
                LocaleKey.NativeChatPlugin_WebBrowser_WebSearch_Searching,
                new DirectResourceKey(query)));

        EnsureConnector();
        count = Math.Clamp(count, 1, _maxSearchCount);

        var results = await _connector.SearchAsync<WebPage>(query, count, 0, cancellationToken).ConfigureAwait(false);
        var indexedResults = results
            .AsValueEnumerable()
            .Select((r, i) => new IndexedWebPage(
                Index: i + 1,
                Name: r.Name,
                Url: r.Url,
                Snippet: r.Snippet))
            .ToList();
        userInterface.DisplaySink.AppendUrls(
            indexedResults.Select(r => new ChatPluginUrl(
                r.Url,
                new DirectResourceKey(r.Name))
            {
                Index = r.Index
            }).ToList());

        return JsonSerializer.Serialize(indexedResults, _jsonSerializerOptions);
    }

    private async ValueTask<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null) return _browser;

        _logger.LogDebug("Ensuring Puppeteer browser is initialized.");

        try
        {
            if (_browserProcess is { HasExited: false })
            {
                // Kill existing browser process if any
                var processId = _browserProcess.Id;
                _browserProcess.Kill();
                _browserProcess = null;
                await _watchdogManager.UnregisterProcessAsync(processId);
            }

            var cachePath = _runtimeConstantProvider.EnsureWritableDataFolderPath("cache/plugins/puppeteer");
            var browserFetcher = new BrowserFetcher
            {
                CacheDir = cachePath,
                Browser = SupportedBrowser.Chromium
            };
            const string buildId = "1499281";
            var executablePath = browserFetcher.GetExecutablePath(buildId);
            if (!File.Exists(executablePath))
            {
                _logger.LogDebug("Downloading Puppeteer browser to cache directory: {CachePath}", cachePath);
                browserFetcher.BaseUrl =
                    await TestUrlConnectionAsync("https://storage.googleapis.com/chromium-browser-snapshots") ??
                    await TestUrlConnectionAsync("https://cdn.npmmirror.com/binaries/chromium-browser-snapshots") ??
                    throw new HandledException(
                        new HttpRequestException("Failed to connect to the Puppeteer browser download URL."),
                        new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                        showDetails: false);
                await browserFetcher.DownloadAsync(buildId);
            }

            _logger.LogDebug("Using Puppeteer browser executable at: {ExecutablePath}", executablePath);
            var launcher = new Launcher(_loggerFactory);
            _browser = await launcher.LaunchAsync(
                new LaunchOptions
                {
                    ExecutablePath = executablePath,
                    Browser = SupportedBrowser.Chromium,
                    Headless = true
                });

            _browserProcess = launcher.Process.Process;
            await _watchdogManager.RegisterProcessAsync(_browserProcess.Id);

            return _browser;
        }
        catch (Exception e)
        {
            throw new HandledException(
                new InvalidOperationException("Failed to download or launch Puppeteer browser.", e),
                new DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_PuppeteerBrowserLaunchError_ErrorMessage),
                showDetails: false);
        }

        async ValueTask<string?> TestUrlConnectionAsync(string testUrl)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10); // Set a reasonable timeout for the test connection
            try
            {
                using var response = await client.GetAsync(testUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return testUrl;
                }

                _logger.LogWarning("Failed to connect to URL: {Url}, Status Code: {StatusCode}", testUrl, response.StatusCode);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to URL: {Url}", testUrl);
                return null;
            }
        }
    }

    [KernelFunction("web_snapshot")]
    [Description("Snapshot accessibility of a web page via Puppeteer, returning a json of the page content and metadata.")]
    [DynamicResourceKey(LocaleKey.NativeChatPlugin_WebBrowser_WebSnapshot_Header, LocaleKey.NativeChatPlugin_WebBrowser_WebSnapshot_Description)]
    private async Task<string> WebSnapshotAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("Web page URL to snapshot")] string url,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Taking web snapshot...");

        _browserDisposer.Cancel();
        await _browserLock.WaitAsync(cancellationToken);

        try
        {
            var browser = await EnsureBrowserAsync(cancellationToken);
            await using var page = await browser.NewPageAsync();

            try
            {
                await page.SetUserAgentAsync(
#if IsWindows
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#elif IsOSX
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#else
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36"
#endif
                );

                userInterface.DisplaySink.AppendDynamicResourceKey(
                    new FormattedDynamicResourceKey(
                        LocaleKey.NativeChatPlugin_WebBrowser_WebSnapshot_Visiting,
                        new DirectResourceKey(url)));

                await page.GoToAsync(url, waitUntil: [WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle2]);

                var node = await page.Accessibility.SnapshotAsync();
                var json = JsonSerializer.Serialize(
                    new WebSnapshotResult(
                        node.Name,
                        node.Children.Select(n => new WebSnapshotElement(
                            n.Name,
                            n.Description,
                            n.Role))),
                    _jsonSerializerOptions);

                return json;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            _browserDisposer.Trigger();
            _browserLock.Release();
        }
    }

    private sealed record IndexedWebPage(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("snippet")] string Snippet
    );

    private sealed record WebSnapshotResult(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("elements")] IEnumerable<WebSnapshotElement> Elements
    );

    private sealed record WebSnapshotElement(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("role")] string Role
    );

    [JsonSerializable(typeof(List<IndexedWebPage>))]
    [JsonSerializable(typeof(WebSnapshotResult))]
    private partial class GeneratedJsonSerializationContext : JsonSerializerContext;

    private class TavilyConnector(string apiKey, HttpClient httpClient, Uri? uri, ILoggerFactory? loggerFactory) : IWebSearchEngineConnector
    {
        private readonly TavilyClient _tavilyClient = new(httpClient, uri);
        private readonly ILogger _logger = loggerFactory?.CreateLogger(typeof(TavilyConnector)) ?? NullLogger.Instance;

        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 21)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 21.");
            }

            _logger.LogDebug("Sending request");

            var response = await _tavilyClient.SearchAsync(apiKey: apiKey, query, maxResults: count, cancellationToken: cancellationToken);

            _logger.LogDebug("Response received");

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = response.Results
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Content)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = response.Results
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Title,
                        Url = x.Url,
                        Snippet = x.Content
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }
    }

    private partial class SearxngConnector(HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory) : IWebSearchEngineConnector
    {
        private readonly ILogger _logger = loggerFactory?.CreateLogger(typeof(SearxngConnector)) ?? NullLogger.Instance;

        public async Task<IEnumerable<T>> SearchAsync<T>(
            string query,
            int count = 1,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 50)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 50.");
            }

            _logger.LogDebug("Sending request: {Uri}", uri);

            using var responseMessage = await httpClient.GetAsync(
                new UriBuilder(uri)
                {
                    Query = $"q={HttpUtility.UrlEncode(query)}&format=json"
                }.Uri,
                cancellationToken).ConfigureAwait(false);

            if (!responseMessage.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"SearXNG API returned error: {responseMessage.StatusCode}");
            }

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);
            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, SearxngResponseJsonSerializationContext.Default.SearxngResponse);
            var data = response?.Data;

            if (data is null || data.Length == 0) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Content)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = GetFormattedPublishedDate(x) switch
                        {
                            { } date => $"{x.Title} (Published: {date})",
                            _ => x.Title
                        },
                        Url = x.Url,
                        Snippet = x.Content
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        private static string? GetFormattedPublishedDate(SearxngSearchResult result)
        {
            return result.PublishedDate1?.ToString("G") ?? result.PublishedDate2?.ToString("G");
        }

        [JsonSerializable(typeof(SearxngResponse))]
        private partial class SearxngResponseJsonSerializationContext : JsonSerializerContext;

        private class SearxngResponse
        {
            [JsonPropertyName("results")]
            public SearxngSearchResult[]? Data { get; init; }
        }

        private sealed class SearxngSearchResult
        {
            /// <summary>
            /// The title of the search result.
            /// </summary>
            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the search result.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The full content of the search result (if available).
            /// </summary>
            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;

            /// <summary>
            /// The publication date of the search result.
            /// </summary>
            [JsonPropertyName("publishedDate")]
            public DateTime? PublishedDate1 { get; set; }

            [JsonPropertyName("pubDate")]
            public DateTime? PublishedDate2 { get; set; }
        }
    }

    private partial class BoChaConnector : IWebSearchEngineConnector
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly Uri _uri;

        /// <summary>
        /// Initializes a new instance of the <see cref="BoChaConnector"/> class.
        /// </summary>
        /// <param name="apiKey">The API key to authenticate the connector.</param>
        /// <param name="httpClient"></param>
        /// <param name="uri">The URI of the Bing Search instance. Defaults to "https://api.bing.microsoft.com/v7.0/search?q".</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
        public BoChaConnector(string apiKey, HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory)
        {
            _httpClient = httpClient;
            _logger = loggerFactory?.CreateLogger(typeof(BoChaConnector)) ?? NullLogger.Instance;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _uri = uri;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 51)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 51.");
            }

            _logger.LogDebug("Sending request: {Uri}", _uri);

            using var responseMessage = await _httpClient.PostAsync(
                _uri,
                JsonContent.Create(
                    new
                    {
                        query,
                        count,
                        summary = true
                    }),
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);

            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, ResponseJsonSerializationContext.Default.Response);
            if (response is not { Data: { } data })
            {
                throw new HttpRequestException(response?.Message);
            }

            if (data?.WebPages?.Value is null) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = data.WebPages.Value
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Summary)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = data.WebPages.Value
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Name,
                        Url = x.Url,
                        Snippet = x.Summary ?? x.Snippet
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        [JsonSerializable(typeof(Response))]
        private partial class ResponseJsonSerializationContext : JsonSerializerContext;

        private class Response
        {
            [JsonPropertyName("code")]
            public int Code { get; init; }

            [JsonPropertyName("msg")]
            public string? Message { get; init; }

            [JsonPropertyName("data")]
            public BoChaWebSearchResponse? Data { get; init; }
        }

        private sealed class BoChaWebSearchResponse
        {
            [JsonPropertyName("webPages")]
            public BoChaWebPages? WebPages { get; set; }
        }

        private sealed class BoChaWebPages
        {
            /// <summary>
            /// a nullable WebPage array object containing the Web Search API response data.
            /// </summary>
            [JsonPropertyName("value")]
            public BoChaWebPage[]? Value { get; set; }
        }

        private sealed class BoChaWebPage
        {
            /// <summary>
            /// The name of the result.
            /// </summary>
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the result.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The result snippet.
            /// </summary>
            [JsonPropertyName("snippet")]
            public string Snippet { get; set; } = string.Empty;

            /// <summary>
            /// The result snippet.
            /// </summary>
            [JsonPropertyName("summary")]
            public string? Summary { get; set; }
        }
    }

    private partial class JinaConnector : IWebSearchEngineConnector
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly Uri _uri;

        /// <summary>
        /// Initializes a new instance of the <see cref="JinaConnector"/> class.
        /// </summary>
        /// <param name="apiKey">The API key to authenticate the connector.</param>
        /// <param name="httpClient"></param>
        /// <param name="uri">The URI of the Jina Search instance. Defaults to "https://s.jina.ai/".</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> to use for logging. If null, no logging will be performed.</param>
        public JinaConnector(string apiKey, HttpClient httpClient, Uri uri, ILoggerFactory? loggerFactory)
        {
            _httpClient = httpClient;
            _logger = loggerFactory?.CreateLogger(typeof(JinaConnector)) ?? NullLogger.Instance;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _uri = uri;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<T>> SearchAsync<T>(string query, int count = 1, int offset = 0, CancellationToken cancellationToken = default)
        {
            if (count is <= 0 or >= 50)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, $"{nameof(count)} value must be greater than 0 and less than 50.");
            }

            _logger.LogDebug("Sending request: {Uri}", _uri);

            using var responseMessage = await _httpClient.PostAsync(
                _uri,
                JsonContent.Create(
                    new
                    {
                        q = query,
                        num = count,
                        page = offset
                    }),
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Response received: {StatusCode}", responseMessage.StatusCode);

            var json = await responseMessage.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Sensitive data, logging as trace, disabled by default
            _logger.LogTrace("Response content received: {Data}", json);

            var response = JsonSerializer.Deserialize(json, JinaResponseJsonSerializationContext.Default.JinaResponse);
            if (response is not { Code: 200, Data: { } data })
            {
                throw new HttpRequestException($"Jina API returned error: {response?.Code}");
            }

            if (data is null || data.Length == 0) return [];

            List<T>? returnValues;
            if (typeof(T) == typeof(string))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => x.Content ?? x.Description)
                    .ToList() as List<T>;
            }
            else if (typeof(T) == typeof(WebPage))
            {
                returnValues = data
                    .AsValueEnumerable()
                    .Take(count)
                    .Select(x => new WebPage
                    {
                        Name = x.Title,
                        Url = x.Url,
                        Snippet = x.Description
                    })
                    .ToList() as List<T>;
            }
            else
            {
                throw new NotSupportedException($"Type {typeof(T)} is not supported.");
            }

            return returnValues ?? [];
        }

        [JsonSerializable(typeof(JinaResponse))]
        private partial class JinaResponseJsonSerializationContext : JsonSerializerContext;

        private class JinaResponse
        {
            [JsonPropertyName("code")]
            public int Code { get; init; }

            [JsonPropertyName("status")]
            public int? Status { get; init; }

            [JsonPropertyName("data")]
            public JinaSearchResult[]? Data { get; init; }
        }

        private sealed class JinaSearchResult
        {
            /// <summary>
            /// The title of the search result.
            /// </summary>
            [JsonPropertyName("title")]
            public string Title { get; set; } = string.Empty;

            /// <summary>
            /// The description/snippet of the search result.
            /// </summary>
            [JsonPropertyName("description")]
            public string Description { get; set; } = string.Empty;

            /// <summary>
            /// The URL of the search result.
            /// </summary>
            [JsonPropertyName("url")]
            public string Url { get; set; } = string.Empty;

            /// <summary>
            /// The full content of the search result (if available).
            /// </summary>
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }
    }
}