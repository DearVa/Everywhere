using Avalonia;
using Avalonia.Controls;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.I18N;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Mac.Chat.Plugin;
using Everywhere.Mac.Common;
using Everywhere.Mac.Configuration;
using Everywhere.Mac.Interop;
using Everywhere.Mac.Patches;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Everywhere.Mac;

public static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        NativeMessageBox.MacOSMessageBoxHandler = MessageBoxHandler;
        await Entrance.InitializeAsync(args);
        InitializeHarmony();

        ServiceLocator.Build(x => x

                #region Basic

                .AddLogging(builder => builder
                    .AddSerilog(dispose: true)
                    .AddFilter<SerilogLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning))
                .AddSingleton<IRuntimeConstantProvider, RuntimeConstantProvider>()
                .AddSingleton<IVisualElementContext, VisualElementContext>()
                .AddSingleton<IShortcutListener, CGEventShortcutListener>()
                .AddSingleton<INativeHelper, NativeHelper>()
                .AddSingleton<IWindowHelper, WindowHelper>()
                .AddSingleton<ISoftwareUpdater, SoftwareUpdater>()
                .AddSettings()
                .AddWatchdogManager()
                .ConfigureNetwork()
                .AddAvaloniaBasicServices()
                .AddViewsAndViewModels()
                .AddDatabaseAndStorage()

                #endregion

                #region Chat Plugins

                .AddTransient<BuiltInChatPlugin, EssentialPlugin>()
                .AddTransient<BuiltInChatPlugin, VisualContextPlugin>()
                .AddTransient<BuiltInChatPlugin, WebBrowserPlugin>()
                .AddTransient<BuiltInChatPlugin, FileSystemPlugin>()
                .AddTransient<BuiltInChatPlugin, SystemPlugin>()
                .AddTransient<BuiltInChatPlugin, ZshPlugin>()

                #endregion

                #region Chat

                .AddSingleton<IKernelMixinFactory, KernelMixinFactory>()
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton<IChatService, ChatService>()
                .AddChatContextManager()

                #endregion

                #region Initialize

                .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
                .AddTransient<IAsyncInitializer, SettingsInitializer>()
                .AddTransient<IAsyncInitializer, UpdaterInitializer>()

            #endregion

        );

        NSApplication.CheckForIllegalCrossThreadCalls = false;
        NSApplication.SharedApplication.Delegate = new AppDelegate();
        NSApplication.Init();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static NativeMessageBoxResult MessageBoxHandler(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
    {
        using var alert = new NSAlert();
        alert.AlertStyle = icon switch
        {
            NativeMessageBoxIcon.Error or NativeMessageBoxIcon.Hand or NativeMessageBoxIcon.Stop => NSAlertStyle.Critical,
            NativeMessageBoxIcon.Warning => NSAlertStyle.Warning,
            _ => NSAlertStyle.Informational
        };
        alert.MessageText = title;
        alert.InformativeText = message;
        switch (buttons)
        {
            case NativeMessageBoxButtons.OkCancel:
            {
                alert.AddButton(LocaleResolver.Common_OK);
                alert.AddButton(LocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.YesNo:
            {
                alert.AddButton(LocaleResolver.Common_Yes);
                alert.AddButton(LocaleResolver.Common_No);
                break;
            }
            case NativeMessageBoxButtons.YesNoCancel:
            {
                alert.AddButton(LocaleResolver.Common_Yes);
                alert.AddButton(LocaleResolver.Common_No);
                alert.AddButton(LocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.RetryCancel:
            {
                alert.AddButton(LocaleResolver.Common_Retry);
                alert.AddButton(LocaleResolver.Common_Cancel);
                break;
            }
            case NativeMessageBoxButtons.AbortRetryIgnore:
            {
                alert.AddButton(LocaleResolver.Common_Abort);
                alert.AddButton(LocaleResolver.Common_Retry);
                alert.AddButton(LocaleResolver.Common_Ignore);
                break;
            }
            default:
            {
                alert.AddButton(LocaleResolver.Common_OK);
                break;
            }
        }
        var result = (NSAlertButtonReturn)alert.RunModal();
        return result switch
        {
            NSAlertButtonReturn.First => buttons switch
            {
                NativeMessageBoxButtons.Ok => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Ok,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Yes,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Retry,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Cancel,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Second => buttons switch
            {
                NativeMessageBoxButtons.OkCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.YesNo => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.No,
                NativeMessageBoxButtons.RetryCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Retry,
                _ => NativeMessageBoxResult.None
            },
            NSAlertButtonReturn.Third => buttons switch
            {
                NativeMessageBoxButtons.YesNoCancel => NativeMessageBoxResult.Cancel,
                NativeMessageBoxButtons.AbortRetryIgnore => NativeMessageBoxResult.Ignore,
                _ => NativeMessageBoxResult.None
            },
            _ => NativeMessageBoxResult.None
        };
    }

    private static void InitializeHarmony()
    {
        // Apply Harmony patches
        var harmony = new Harmony("com.sylinko.everywhere.mac.patches");
        ControlAutomationPeerPatch.Patch(harmony);
        BclLauncherExecPatch.Patch(harmony);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(
                new AvaloniaNativePlatformOptions
                {
                    AppSandboxEnabled = false
                })
            .With(
                new MacOSPlatformOptions
                {
                    // These settings are important for showing chat window over other fullscreen apps
                    ShowInDock = false,
                    DisableAvaloniaAppDelegate = true
                })
            .WithInterFont()
            .LogToTrace();
}