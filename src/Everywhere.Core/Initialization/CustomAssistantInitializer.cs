using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Initialization;

public class CustomAssistantInitializer(Settings settings) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings + 1;

    public Task InitializeAsync()
    {
        foreach (var customAssistant in settings.Model.CustomAssistants)
        {
            customAssistant.Configurator.Initialize();
        }

        return Task.CompletedTask;
    }
}