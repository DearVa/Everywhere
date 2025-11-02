using System;
using System.Net;
using System.Net.Http;

namespace Everywhere.Configuration;

/// <summary>
/// Creates HTTP clients and handlers that respect the current proxy configuration.
/// </summary>
internal static class ProxyHttpClientFactory
{
    public static HttpClient CreateHttpClient(Action<SocketsHttpHandler>? configureHandler = null)
    {
        return new HttpClient(CreateSocketsHttpHandler(configureHandler), disposeHandler: true);
    }

    public static SocketsHttpHandler CreateSocketsHttpHandler(Action<SocketsHttpHandler>? configureHandler = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        ApplyProxy(handler);
        configureHandler?.Invoke(handler);

        return handler;
    }

    public static void ApplyProxy(SocketsHttpHandler handler)
    {
        var proxy = NetworkProxyConfigurator.CurrentProxy;
        handler.Proxy = proxy;
        handler.UseProxy = proxy is not null;
    }
}