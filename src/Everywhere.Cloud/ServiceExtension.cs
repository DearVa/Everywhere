using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCloudClient()
        {
            services.AddSingleton<ICloudClient, HttpCloudClient>();
            return services;
        }
    }
}