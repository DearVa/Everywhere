using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Chat.Permissions;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Brave;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using PuppeteerSharp;
using ZLinq;
using IHttpClientFactory = System.Net.Http.IHttpClientFactory;

namespace Everywhere.Chat.Plugins;

public partial class WebBrowserPlugin : BuiltInChatPlugin
{
    public override DynamicResourceKeyBase HeaderKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_Header);
    public override DynamicResourceKeyBase DescriptionKey { get; } = new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_Description);
    public override LucideIconKind? Icon => LucideIconKind.Globe;

    public override IReadOnlyList<SettingsItem> SettingsItems => _webSearchEngineSettings.SettingsItems;

    private readonly WebSearchEngineSettings _webSearchEngineSettings;
    private readonly IRuntimeConstantProvider _runtimeConstantProvider;
    private readonly IWatchdogManager _watchdogManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebBrowserPlugin> _logger;
    private readonly DebounceExecutor<WebBrowserPlugin, ThreadingTimerImpl> _browserDisposer;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = WebBrowserPluginJsonSerializationContext.Default
    };

    private readonly SemaphoreSlim _browserLock = new(1, 1);

    private IWebSearchEngineConnector? _connector;
    private int _maxSearchCount;
    private string? _previousLaunchedBrowserPath;
    private IBrowser? _browser;
    private Process? _browserProcess;

    static WebBrowserPlugin()
    {
        // Suppress unobserved Puppeteer exceptions
        Entrance.UnobservedTaskExceptionFilter += (_, e) =>
        {
            if (!e.Observed && e.Exception.Segregate().AsValueEnumerable().Any(ex => ex is PuppeteerException)) e.SetObserved();
        };
    }

    public WebBrowserPlugin(
        Settings settings,
        IRuntimeConstantProvider runtimeConstantProvider,
        IWatchdogManager watchdogManager,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory) : base("web_browser")
    {
        _webSearchEngineSettings = settings.Plugin.WebSearchEngine;
        _runtimeConstantProvider = runtimeConstantProvider;
        _watchdogManager = watchdogManager;
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
                        that._watchdogManager.UnregisterProcessAsync(that._browserProcess.Id); // Kill if running
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

        if (_webSearchEngineSettings.SelectedProvider is not { } provider)
        {
            throw new HandledException(
                new ArgumentException("Web search engine provider is not selected."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_NoWebSearchEngineProviderSelected_ErrorMessage),
                showDetails: false);
        }

        if (!Uri.TryCreate(provider.EndPoint.ActualValue, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https")
        {
            throw new HandledException(
                new ArgumentException("Endpoint is not a valid absolute URI."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_InvalidWebSearchEngineEndpoint_ErrorMessage),
                showDetails: false);
        }

        // Extract only the base URI without query parameters
        uri = new UriBuilder(uri) { Query = string.Empty }.Uri;

        (_connector, _maxSearchCount) = provider.Id switch
        {
            WebSearchEngineProviderId.Google => (new GoogleConnector(
                EnsureApiKey(provider.ApiKey),
                provider.SearchEngineId ??
                throw new HandledException(
                    new UnauthorizedAccessException("Search Engine ID is not set."),
                    new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_GoogleSearchEngineIdNotSet_ErrorMessage),
                    showDetails: false),
                _httpClientFactory.CreateClient(),
                uri,
                _loggerFactory) as IWebSearchEngineConnector, 10),
            WebSearchEngineProviderId.Tavily => (
                new TavilyConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), uri, _loggerFactory), 20),
            WebSearchEngineProviderId.Brave => (
                new BraveConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), new Uri(uri, "?q"), _loggerFactory), 20),
            WebSearchEngineProviderId.Bocha => (
                new BoChaConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), uri, _loggerFactory), 50),
            WebSearchEngineProviderId.Jina => (
                new JinaConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), new Uri(uri, "?q"), _loggerFactory), 50),
            WebSearchEngineProviderId.UniFuncs => (
                new UniFuncsConnector(EnsureApiKey(provider.ApiKey), _httpClientFactory.CreateClient(), uri, _loggerFactory), 50),
            WebSearchEngineProviderId.SearXNG => (new SearxngConnector(_httpClientFactory.CreateClient(), uri, _loggerFactory), 50),
            _ => throw new HandledException(
                new NotSupportedException($"Web search engine provider '{provider.Id}' is not supported."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_UnsupportedWebSearchEngineProvider_ErrorMessage),
                showDetails: false)
        };

        string EnsureApiKey(Guid id) =>
            ApiKey.GetKey(id) ??
                throw new HandledException(
                    new UnauthorizedAccessException("API key is not set."),
                    new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_WebSearchEngineApiKeyNotSet_ErrorMessage),
                    showDetails: false);
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
        "Searches the public web for real-time information. Returns a JSON array of web pages. " +
        "STRICTLY confined to internet content; DO NOT use to search local files or personal data. " +
        "Results may be inaccurate.")]
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_WebSearch_Header, LocaleKey.BuiltInChatPlugin_WebBrowser_WebSearch_Description)]
    private async Task<string> WebSearchAsync(
        [FromKernelServices] IChatPluginUserInterface userInterface,
        [Description("Search query")] string query,
        [Description("Number of results")] int count = 10,
        CancellationToken cancellationToken = default) // TODO: Offset is not well supported.
    {
        _logger.LogDebug("Performing web search with query: {Query}, count: {Count}", query, count);

        userInterface.DisplaySink.AppendDynamicResourceKey(
            new FormattedDynamicResourceKey(
                LocaleKey.BuiltInChatPlugin_WebBrowser_WebSearch_Searching,
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

        if (_browserProcess is { HasExited: false })
        {
            // Kill existing browser process if any
            var processId = _browserProcess.Id;
            _browserProcess = null;
            await _watchdogManager.UnregisterProcessAsync(processId); // Kill if running
        }

        // First try to launch previously launched browser
        _browser = await TryLaunchBrowserAsync(_previousLaunchedBrowserPath, SupportedBrowser.Chromium);
        if (_browser is not null) return _browser;

        // Then try to launch installed Edge browser
        _browser = await TryLaunchBrowserAsync(BrowserHelper.GetEdgePath(), SupportedBrowser.Chromium);
        if (_browser is not null) return _browser;

        // Then try to launch installed Chrome browser
        _browser = await TryLaunchBrowserAsync(BrowserHelper.GetChromePath(), SupportedBrowser.Chrome);
        if (_browser is not null) return _browser;

        // Finally download and launch Puppeteer browser
        var cachePath = _runtimeConstantProvider.EnsureWritableDataFolderPath("cache/plugins/puppeteer");
        var browserFetcher = new BrowserFetcher
        {
            CacheDir = cachePath,
            Browser = SupportedBrowser.Chromium
        };
        const string buildId = "1499281";
        var executablePath = browserFetcher.GetExecutablePath(buildId);

        // Try to launch again in case the browser was downloaded previously
        _browser = await TryLaunchBrowserAsync(executablePath, SupportedBrowser.Chrome);
        if (_browser is not null) return _browser;

        // We use two different URLs to download the browser for better reliability
        _logger.LogDebug("Downloading Puppeteer browser to cache directory: {CachePath}", cachePath);
        browserFetcher.BaseUrl =
            await TestUrlConnectionAsync("https://storage.googleapis.com/chromium-browser-snapshots") ??
            await TestUrlConnectionAsync("https://cdn.npmmirror.com/binaries/chromium-browser-snapshots") ??
            throw new HandledException(
                new HttpRequestException("Failed to connect to the Puppeteer browser download URL."),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);

        try
        {
            await browserFetcher.DownloadAsync(buildId);
        }
        catch (Exception e)
        {
            throw new HandledException(
                new InvalidOperationException("Failed to download Puppeteer browser.", e),
                new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_PuppeteerBrowserDownloadConnectionError_ErrorMessage),
                showDetails: true);
        }

        // Try to launch again after download
        _browser = await TryLaunchBrowserAsync(executablePath, SupportedBrowser.Chromium);
        if (_browser is not null) return _browser;

        throw new HandledException(
            new InvalidOperationException("All attempts to launch Puppeteer browser have failed."),
            new DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_PuppeteerBrowserLaunchError_ErrorMessage),
            showDetails: true);

        async ValueTask<IBrowser?> TryLaunchBrowserAsync(string? path, SupportedBrowser browserType)
        {
            if (path.IsNullOrEmpty()) return null;
            if (!File.Exists(path)) return null;

            try
            {
                _logger.LogDebug("Try launch Puppeteer browser executable at: {Path}", path);
                var launcher = new Launcher(_loggerFactory);
                _browser = await launcher.LaunchAsync(
                    new LaunchOptions
                    {
                        ExecutablePath = path,
                        Browser = browserType,
                        Headless = true
                    });

                _previousLaunchedBrowserPath = path;
                _browserProcess = launcher.Process.Process;
                await _watchdogManager.RegisterProcessAsync(_browserProcess.Id);

                return _browser;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to launch Puppeteer browser at: {Path}", path);
                return null;
            }
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
    [DynamicResourceKey(LocaleKey.BuiltInChatPlugin_WebBrowser_WebSnapshot_Header, LocaleKey.BuiltInChatPlugin_WebBrowser_WebSnapshot_Description)]
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
                        LocaleKey.BuiltInChatPlugin_WebBrowser_WebSnapshot_Visiting,
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
    private partial class WebBrowserPluginJsonSerializationContext : JsonSerializerContext;

    private static class BrowserHelper
    {
        public static string? GetChromePath()
        {
            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => SearchWindowsApplicationPath(Path.Combine("Google", "Chrome", "Application", "chrome.exe")),
                PlatformID.MacOSX => SearchMacOSApplicationPath("Google Chrome"),
                PlatformID.Unix => SearchLinuxApplicationPath("google-chrome"),
                _ => null
            };
        }

        public static string? GetEdgePath()
        {
            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => SearchWindowsApplicationPath(Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")),
                PlatformID.MacOSX => SearchMacOSApplicationPath("Microsoft Edge"),
                PlatformID.Unix => SearchLinuxApplicationPath("microsoft-edge-stable"),
                _ => null
            };
        }

        private static string? SearchWindowsApplicationPath(string relativePath)
        {
            Span<Environment.SpecialFolder> rootPaths =
            [
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86
            ];
            return rootPaths
                .AsValueEnumerable()
                .Select(rootPath => Path.Combine(
                    Environment.GetFolderPath(rootPath),
                    relativePath))
                .FirstOrDefault(File.Exists);
        }

        private static string? SearchMacOSApplicationPath(string appName)
        {
            var path = $"/Applications/{appName}.app/Contents/MacOS/{appName}";
            return File.Exists(path) ? path : null;
        }

        private static string? SearchLinuxApplicationPath(string executableName)
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(':') ?? [];
            return paths
                .AsValueEnumerable()
                .Select(path => Path.Combine(path, executableName))
                .FirstOrDefault(File.Exists);
        }
    }
}