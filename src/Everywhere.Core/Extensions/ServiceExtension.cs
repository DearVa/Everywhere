using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Database;
using Everywhere.Storage;
using Everywhere.Views;
using Everywhere.Views.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Extensions;

public static class ServiceExtension
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAvaloniaBasicServices() =>
            services
                .AddDialogManagerAndToastManager()
                .AddTransient<IClipboard>(_ =>
                    Application.Current.As<App>()?.TopLevel.Clipboard ??
                    throw new InvalidOperationException("Clipboard is not available."))
                .AddTransient<IStorageProvider>(_ =>
                    Application.Current.As<App>()?.TopLevel.StorageProvider ??
                    throw new InvalidOperationException("StorageProvider is not available."))
                .AddTransient<ILauncher>(_ =>
                    Application.Current.As<App>()?.TopLevel.Launcher ??
                    throw new InvalidOperationException("Launcher is not available."));

        public IServiceCollection AddViewsAndViewModels() =>
            services
                .AddSingleton<VisualTreeDebugger>()
                .AddSingleton<ChatWindowViewModel>()
                .AddSingleton<ChatWindow>()
                .AddTransient<IMainViewPageFactory, SettingsCategoryPageFactory>()
                .AddSingleton<CustomAssistantPageViewModel>()
                .AddSingleton<IMainViewPage, CustomAssistantPage>()
                .AddSingleton<ChatPluginPageViewModel>()
                .AddSingleton<IMainViewPage, ChatPluginPage>()
                .AddSingleton<AboutPageViewModel>()
                .AddSingleton<IMainViewPage, AboutPage>()
                .AddSingleton<UserProfileFlyoutViewModel>()
                .AddTransient<UserProfileFlyoutContent>()
                .AddTransient<WelcomeViewModel>()
                .AddTransient<WelcomeView>()
                .AddTransient<ChangeLogViewModel>()
                .AddTransient<ChangeLogView>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<MainView>();

        public IServiceCollection AddDatabaseAndStorage() =>
            services
                .AddDbContextFactory<ChatDbContext>((_, options) =>
                {
                    var dbPath = RuntimeConstants.GetDatabasePath("chat.db");
                    options.UseSqlite($"Data Source={dbPath}");
                })
                .AddSingleton<IBlobStorage, BlobStorage>()
                .AddSingleton<IChatContextStorage, ChatContextStorage>()
                .AddTransient<IAsyncInitializer, ChatDbInitializer>();
    }
}