using System.Net;
using Everywhere.Initialization;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Chat.Plugins.McpExtensions;

public static class McpServiceExtension
{
    /// <summary>
    /// Registers the HttpClient and handlers needed for MCP HTTP transports.
    /// </summary>
    public static IServiceCollection AddManagedMcp(this IServiceCollection services)
    {
        services.AddTransient<McpSessionExpiryHandler>();
        services.AddTransient<NetworkExtension.ContentLengthBufferingHandler>();

        services
            .AddHttpClient(
                NetworkExtension.JsonRpcClientName,
                client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                new HttpClientHandler
                {
                    Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                    UseProxy = true,
                    AllowAutoRedirect = true,
                })
            .AddHttpMessageHandler<McpSessionExpiryHandler>()
            .AddHttpMessageHandler<NetworkExtension.ContentLengthBufferingHandler>();

        return services;
    }
}
