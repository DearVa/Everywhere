using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AttachedProperties;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Serilog;
using ShadUI;
using Window = Avalonia.Controls.Window;

namespace Everywhere;

public class App : Application, IRecipient<ApplicationCommand>
{
    public static string Version => typeof(TransientWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static ThemeManager ThemeManager => _themeManager ?? throw new InvalidOperationException("ThemeManager is not initialized.");

    private static ThemeManager? _themeManager;

    public TopLevel TopLevel { get; } = new Window();

    private readonly DebounceExecutor<App, DispatcherTimerImpl> _trayIconClickedDebounce;

    private TransientWindow? _mainWindow, _debugWindow;
    private int _trayIconClickCount;

    public App()
    {
        _trayIconClickedDebounce = new DebounceExecutor<App, DispatcherTimerImpl>(
            () => this,
            app =>
            {
                if (app._trayIconClickCount >= 2)
                {
                    app.HandleOpenMainWindowMenuItemClicked(app, EventArgs.Empty);
                }
                else
                {
                    app.HandleOpenChatWindowMenuItemClicked(app, EventArgs.Empty);
                }

                app._trayIconClickCount = 0;
            },
            TimeSpan.FromMilliseconds(300)
        );
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // After this, ThemeChanged event from the system can be received
        _themeManager = new ThemeManager(this);

        // Register to receive application commands
        // e.g. ShowMainWindow
        WeakReferenceMessenger.Default.Register(this);

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Logger.Error(e.Exception, "UI Thread Unhandled Exception");

            NativeMessageBox.Show(
                "Unexpected Error",
                $"An unexpected error occurred:\n{e.Exception.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);

            e.Handled = true;
        };

        MarkdownNode.Register<MathInlineNode>();
        MarkdownNode.Register<MathBlockNode>();

        try
        {
            foreach (var group in ServiceLocator
                         .Resolve<IEnumerable<IAsyncInitializer>>()
                         .GroupBy(i => i.Priority)
                         .OrderBy(g => g.Key))
            {
                Task.WhenAll(group.Select(i => i.InitializeAsync())).WaitOnDispatcherFrame();
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal(ex, "Failed to initialize application");

            NativeMessageBox.Show(
                "Initialization Error",
                $"An error occurred during application initialization:\n{ex.Message}\n\nPlease check the logs for more details.",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }

        Log.ForContext<App>().Information("Application started");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime:
            {
                DisableAvaloniaDataAnnotationValidation();
                ShowMainWindowOnNeeded();
                break;
            }
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToList();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    /// <summary>
    /// Show the main window if it was not shown before or the version has changed.
    /// </summary>
    private void ShowMainWindowOnNeeded()
    {
        // If the --ui command line argument is present, show the main window.
        if (Environment.GetCommandLineArgs().Contains("--ui"))
        {
            ShowWindow<MainView>(ref _mainWindow);
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        var persistentState = ServiceLocator.Resolve<PersistentState>();
        if (persistentState.PreviousLaunchVersion == version) return;

        ShowWindow<MainView>(ref _mainWindow);
    }

    private void HandleTrayIconClicked(object? sender, EventArgs e)
    {
        _trayIconClickCount++;
        if (_trayIconClickCount >= 2)
        {
            // Double click detected, open main window immediately.
            HandleOpenMainWindowMenuItemClicked(this, EventArgs.Empty);
            _trayIconClickCount = 0;
            _trayIconClickedDebounce.Cancel();
        }
        else
        {
            // Start or reset the debounce timer for single click.
            _trayIconClickedDebounce.Trigger();
        }
    }

    private void HandleOpenChatWindowMenuItemClicked(object? sender, EventArgs e)
    {
        var chatWindow = ServiceLocator.Resolve<ChatWindow>();
        chatWindow.ViewModel.ShowAsync(null).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private void HandleOpenMainWindowMenuItemClicked(object? sender, EventArgs e)
    {
        ShowWindow<MainView>(ref _mainWindow);
    }

    private void HandleOpenDebugWindowMenuItemClicked(object? sender, EventArgs e)
    {
        ShowWindow<VisualTreeDebugger>(ref _debugWindow);
    }

    /// <summary>
    /// Flag to prevent multiple calls to ShowWindow method from event loop.
    /// </summary>
    private static bool _isShowWindowBusy;

    private static void ShowWindow<TContent>(ref TransientWindow? window) where TContent : Control
    {
        if (_isShowWindowBusy) return;
        try
        {
            _isShowWindowBusy = true;
            if (window is { IsVisible: true })
            {
                if (window.WindowState is WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                var windowHelper = ServiceLocator.Resolve<IWindowHelper>();
                windowHelper.SetCloaked(window, false);
            }
            else
            {
                window?.Close();
                var content = ServiceLocator.Resolve<TContent>();
                content.To<ISetLogicalParent>().SetParent(null);
                window = new TransientWindow
                {
                    [SaveWindowPlacementAssist.KeyProperty] = typeof(TContent).FullName,
                    Content = content
                };
                window.Show();
            }
        }
        finally
        {
            _isShowWindowBusy = false;
        }
    }

    private void HandleExitMenuItemClicked(object? sender, EventArgs e)
    {
        Environment.Exit(0);
    }

    public void Receive(ApplicationCommand command)
    {
        if (command is ShowWindowCommand { Name: nameof(MainView) })
        {
            Dispatcher.UIThread.Invoke(() => ShowWindow<MainView>(ref _mainWindow));
        }
    }
}