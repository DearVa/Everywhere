using System.Net;
using Everywhere.Common;
using Everywhere.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCloudClient()
        {
            services.AddSingleton<OAuthCloudClient>();
            services.AddSingleton<ICloudClient>(x => x.GetRequiredService<OAuthCloudClient>());
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<OAuthCloudClient>());

            services.AddSingleton<IChatDbSynchronizer, CloudChatDbSynchronizer>();
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<IChatDbSynchronizer>());

            // Register the authenticated HttpClient for API requests.
            // This client includes the CloudAuthenticationHandler which:
            // - Automatically adds Bearer token to Authorization header
            // - Handles 401 responses by refreshing the token and retrying
            // Note: Authentication flows (login, token refresh) use the default HttpClient
            // which is already configured with proxy in NetworkInitializer.
            services
                .AddHttpClient(
                    nameof(ICloudClient),
                    client =>
                    {
                        client.Timeout = TimeSpan.FromSeconds(30);
                        var version = typeof(ServiceExtension).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
                        client.DefaultRequestHeaders.Add(
                            "User-Agent",
                            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Everywhere/{version}");
                    })
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                    new HttpClientHandler
                    {
                        Proxy = serviceProvider.GetRequiredService<IWebProxy>(),
                        UseProxy = true,
                        AllowAutoRedirect = true,
                    })
                .AddHttpMessageHandler(x => x.GetRequiredService<ICloudClient>().CreateAuthenticationHandler());

            return services;
        }
    }
}