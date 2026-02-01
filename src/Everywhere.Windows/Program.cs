using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Avalonia;
using Avalonia.Controls;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Extensions;
using Everywhere.Initialization;
using Everywhere.Interop;
using Everywhere.Windows.Chat.Plugins;
using Everywhere.Windows.Common;
using Everywhere.Windows.Configuration;
using Everywhere.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace Everywhere.Windows;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task MainAsync(string[] args)
    {
        if (args.Contains("--load-user-profile"))
        {
            LoadUserProfile();
        }

        await Entrance.InitializeAsync(args);

        ServiceLocator.Build(x => x

                #region Basic

                .AddLogging(builder => builder
                    .AddSerilog(dispose: true)
                    .AddFilter<SerilogLoggerProvider>("Microsoft.EntityFrameworkCore", LogLevel.Warning))
                .AddSingleton<IRuntimeConstantProvider, RuntimeConstantProvider>()
                .AddSingleton<IVisualElementContext, VisualElementContext>()
                .AddSingleton<IShortcutListener, ShortcutListener>()
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
                .AddTransient<BuiltInChatPlugin, PowerShellPlugin>()
                .AddTransient<BuiltInChatPlugin, EverythingPlugin>()

                #endregion

                #region Chat

                .AddSingleton<IKernelMixinFactory, KernelMixinFactory>()
                .AddSingleton<IChatPluginManager, ChatPluginManager>()
                .AddSingleton<IChatService, ChatService>()
                .AddChatContextManager()

                #endregion

                #region Initialize

                .AddTransient<IAsyncInitializer, ChatWindowInitializer>()
                .AddTransient<IAsyncInitializer, UpdaterInitializer>()

            #endregion

        );

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// -----------------------------------------------------------------------------------------
    /// THE PROBLEM (ERROR 1312 - ERROR_NO_SUCH_LOGON_SESSION):
    /// When a process is spawned automatically by the Task Scheduler in a "Highest Privileges" context,
    /// Windows creates a specialized Logon Session. Often, for performance or security reasons (S4U),
    /// the User Profile Service does NOT fully load the user's registry hive (HKCU) or the DPAPI
    /// Master Keyring into the Local Security Authority (LSA) memory subsystem.
    ///
    /// Without this crypto context, the application has the correct User SID and Admin Token, but
    /// strictly lacks the cryptographic keys required to access the Windows Credential Manager or
    /// decrypt data protected by user-scope DPAPI. Attempts to call `CredWrite` or `CryptProtectData`
    /// fail immediately with error 1312.
    ///
    /// THE SOLUTION (FORCED PROFILE LOADING):
    /// By calling LoadUserProfileW here, we explicitly instruct the User Profile Service to:
    /// 1. Mount the user's NTUSER.DAT registry hive.
    /// 2. Decrypt and verify the user's Master Key using the logon credentials.
    /// 3. Inject this cryptographic context into the new process's session.
    /// -----------------------------------------------------------------------------------------
    /// </summary>
    private static unsafe void LoadUserProfile()
    {
        var token = WindowsIdentity.GetCurrent().Token;
        fixed (char* pUserName = Environment.UserName)
        {
            var profileInfo = new PROFILEINFOW
            {
                dwSize = (uint)sizeof(PROFILEINFOW),
                lpUserName = pUserName,
                dwFlags = 0,
            };
            PInvoke.LoadUserProfile((HANDLE)token, &profileInfo);
        }
    }
}