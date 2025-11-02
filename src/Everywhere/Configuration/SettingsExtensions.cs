using System.Text.Json;
using Everywhere.Common;
using Everywhere.Initialization;
using Everywhere.Views.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WritableJsonConfiguration;

namespace Everywhere.Configuration;

public static class SettingsExtensions
{
    public static IServiceCollection AddSettings(this IServiceCollection services) => services
        .AddKeyedSingleton<IConfiguration>(
            typeof(Settings),
            (xx, _) =>
            {
                IConfiguration configuration;
                var settingsJsonPath = Path.Combine(
                    xx.GetRequiredService<IRuntimeConstantProvider>().Get<string>(RuntimeConstantType.WritableDataPath),
                    "settings.json");
                try
                {
                    configuration = WritableJsonConfigurationFabric.Create(settingsJsonPath);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    File.Delete(settingsJsonPath);
                    configuration = WritableJsonConfigurationFabric.Create(settingsJsonPath);
                }
                return configuration;
            })
        .AddSingleton<Settings>(xx =>
        {
            var configuration = xx.GetRequiredKeyedService<IConfiguration>(typeof(Settings));
            var settings = new Settings();
            configuration.Bind(settings);
            return settings;
        })
        .AddTransient<SoftwareUpdateControl>()
        .AddTransient<RestartAsAdministratorControl>()
        .AddSingleton<NetworkProxyInitializer>()
        .AddSingleton<IAsyncInitializer>(x => x.GetRequiredService<NetworkProxyInitializer>())
        .AddTransient<IAsyncInitializer, SettingsInitializer>();
}