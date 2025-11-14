using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using MsBox.Avalonia.Enums;
using Serilog;
using Window = Avalonia.Controls.Window;

namespace Everywhere;

public class App : Application
{
    public TopLevel TopLevel { get; } = new Window();

    private TransientWindow? _mainWindow, _debugWindow;

    public override void Initialize()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Logger.Error(e.Exception, "UI Thread Unhandled Exception");

            NativeMessageBox.ShowAsync(
                "Unexpected Error",
                $"An unexpected error occurred:\n{e.Exception.Message}\n\nPlease check the logs for more details.",
                ButtonEnum.Ok,
                Icon.Error).WaitOnDispatcherFrame();

            e.Handled = true;
        };

        AvaloniaXamlLoader.Load(this);

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

            NativeMessageBox.ShowAsync(
                "Initialization Error",
                $"An error occurred during application initialization:\n{ex.Message}\n\nPlease check the logs for more details.",
                ButtonEnum.Ok,
                Icon.Error).WaitOnDispatcherFrame();
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
        var settings = ServiceLocator.Resolve<Settings>();
        if (settings.Internal.PreviousLaunchVersion == version) return;

        ShowWindow<MainView>(ref _mainWindow);
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
                var topmost = window.Topmost;
                window.Topmost = false;
                window.Activate();
                window.Topmost = true;
                window.Topmost = topmost;
            }
            else
            {
                window?.Close();
                var content = ServiceLocator.Resolve<TContent>();
                content.To<ISetLogicalParent>().SetParent(null);
                window = new TransientWindow
                {
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
}