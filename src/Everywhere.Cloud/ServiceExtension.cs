using Duende.IdentityModel.OidcClient;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Cloud;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCloudClient()
        {
            services.AddSingleton(new OidcClient(new OidcClientOptions
            {
                Authority = "https://sy.com",

                ClientId = "interactive.public",
                Scope = "openid profile api",
                RedirectUri = "sylinko-everywhere://callback"
            }));
            return services;
        }
    }
}