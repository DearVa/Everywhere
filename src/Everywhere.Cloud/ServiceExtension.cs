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
            services.AddSingleton<ICloudClient, OAuthCloudClient>();
            services.AddSingleton<IChatDbSynchronizer, CloudChatDbSynchronizer>();
            services.AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<IChatDbSynchronizer>());
            return services;
        }
    }
}