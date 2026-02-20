using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShadUI;

namespace Everywhere.Initialization;

/// <summary>
/// Applies the configured network proxy to global HTTP handlers and keeps it in sync with user settings.
/// </summary>
public sealed class NetworkInitializer : IAsyncInitializer
{
    public AsyncInitializerPriority Priority => AsyncInitializerPriority.AfterSettings;

    private readonly ProxySettings _proxySettings;
    private readonly DynamicWebProxy _dynamicWebProxy;
    private readonly ILogger<NetworkInitializer> _logger;
    private readonly DebounceExecutor<NetworkInitializer, ThreadingTimerImpl> _applyProxyDebounceExecutor;

    public NetworkInitializer(Settings settings, DynamicWebProxy dynamicWebProxy, ILogger<NetworkInitializer> logger)
    {
        _proxySettings = settings.Common.Proxy;
        _dynamicWebProxy = dynamicWebProxy;
        _logger = logger;
        _applyProxyDebounceExecutor = new DebounceExecutor<NetworkInitializer, ThreadingTimerImpl>(
            () => this,
            static that => that.ApplyProxySettings(true),
            TimeSpan.FromSeconds(0.5));
    }

    public Task InitializeAsync()
    {
        ApplyProxySettings(false);

        // Use ObjectObserver to observe property changes (including nested properties, e.g. Customizable).
        new ObjectObserver(HandleProxySettingsChanged).Observe(_proxySettings);

        return Task.CompletedTask;
    }

    private void HandleProxySettingsChanged(in ObjectObserverChangedEventArgs e)
    {
        _applyProxyDebounceExecutor.Trigger();
    }

    private void ApplyProxySettings(bool notifyOnError)
    {
        try
        {
            _dynamicWebProxy.ApplyProxySettings(_proxySettings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to apply proxy settings: {Message}", ex);

            if (notifyOnError)
            {
                Dispatcher.UIThread.InvokeOnDemand(() =>
                {
                    ServiceLocator
                        .Resolve<ToastManager>()
                        .CreateToast(LocaleResolver.Common_Error)
                        .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                        .DismissOnClick()
                        .OnBottomRight()
                        .ShowError();
                });
            }
        }
    }
}

/// <summary>
/// Extension methods for configuring network services.
/// </summary>
public static class NetworkExtension
{
    /// <summary>
    /// The name for the HttpClient configured to handle JSON-RPC requests
    /// by ensuring Content-Length is set, avoiding chunked encoding.
    /// </summary>
    public const string JsonRpcClientName = "JsonRpcClient";

    /// <summary>
    /// Configures network services with proxy settings from the application settings.
    /// This method registers a singleton <see cref="DynamicWebProxy"/> to handle proxying HTTP requests.
    /// It also configures the default <see cref="HttpClient"/> to use this dynamic proxy.
    /// Then adds the <see cref="NetworkInitializer"/> to handle dynamic changes and initial application of proxy settings.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection ConfigureNetwork(this IServiceCollection services)
    {
        // Register DynamicWebProxy as a singleton to manage proxy settings.
        services
            .AddSingleton<DynamicWebProxy>()
            .AddSingleton<IWebProxy>(x => x.GetRequiredService<DynamicWebProxy>())
            .AddSingleton<UserAgentHandler>()
            .AddSingleton<ContentLengthBufferingHandler>()
            .AddTransient<IAsyncInitializer, NetworkInitializer>();

        // Configure the default HttpClient to use the DynamicWebProxy.
        services
            .AddHttpClient(
                Options.DefaultName,
                client =>
                {
                    // Set a short timeout for HTTP requests.
                    client.Timeout = TimeSpan.FromSeconds(10);
                })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new HttpClientHandler
                {
                    // Resolve the singleton instance of DynamicWebProxy.
                    Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                    UseProxy = true,
                    AllowAutoRedirect = true,
                })
            .AddHttpMessageHandler<UserAgentHandler>();

        // This is a workaround for JSON-RPC servers that do not support chunked transfer encoding.
        // e.g. MCP server of ModelScope
        // We create a named HttpClient that includes a custom DelegatingHandler to buffer
        // the request content and set the Content-Length header explicitly.
        services
            .AddHttpClient(
                JsonRpcClientName,
                client =>
                {
                    // You can copy or customize headers from the default client if needed.
                    client.Timeout = TimeSpan.FromSeconds(30); // Maybe a longer timeout for RPC calls
                })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new HttpClientHandler
                {
                    Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                    UseProxy = true,
                    AllowAutoRedirect = true,
                })
            // Add our custom handler to the pipeline for this named client.
            .AddHttpMessageHandler<ContentLengthBufferingHandler>();

        return services;
    }

    /// <summary>
    /// Force override the User-Agent before send
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class UserAgentHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.Add(
                "User-Agent",
                $"Chrome/142.0.0.0 Safari/537.36 Everywhere/{App.Version}");

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A delegating handler that buffers the request content to compute and set the
    /// Content-Length header. This is useful for servers that do not support
    /// chunked transfer encoding.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private sealed class ContentLengthBufferingHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                // By calling LoadIntoBufferAsync, we force the content to be buffered in memory.
                // This allows the HttpContent instance to calculate its length, which then gets
                // automatically set as the Content-Length header when the request is sent.
                // This effectively disables chunked transfer encoding.
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}