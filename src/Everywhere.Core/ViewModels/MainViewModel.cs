using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.ViewModels;

public sealed partial class MainViewModel : ReactiveViewModelBase, IDisposable
{
    [ObservableProperty] public partial NavigationBarItem? CurrentItem { get; set; }

    public ReadOnlyObservableCollection<NavigationBarItem> Items { get; }

    public ICloudClient CloudClient { get; }

    /// <summary>
    /// Use public property for MVVM binding
    /// </summary>
    public PersistentState PersistentState { get; }

    private readonly SourceList<NavigationBarItem> _itemsSource = new();
    private readonly CompositeDisposable _disposables = new(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;

    public MainViewModel(
        ICloudClient cloudClient,
        IServiceProvider serviceProvider,
        Settings settings,
        PersistentState persistentState)
    {
        CloudClient = cloudClient;

        _serviceProvider = serviceProvider;
        _settings = settings;
        PersistentState = persistentState;

        Items = _itemsSource
            .Connect()
            .ObserveOnDispatcher()
            .BindEx(_disposables);
    }

    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        if (_itemsSource.Count > 0)
        {
            await base.ViewLoaded(cancellationToken);
            return;
        }

        // Try to restore user session with silent login (fire and forget)
        _ = CloudClient.TrySilentLoginAsync();

        _itemsSource.AddRange(
            _serviceProvider
                .GetServices<IMainViewPageFactory>()
                .SelectMany(f => f.CreatePages())
                .Concat(_serviceProvider.GetServices<IMainViewPage>())
                .OrderBy(p => p.Index)
                .Select(p => new NavigationBarItem
                {
                    Content = p.Title.ToTextBlock(),
                    Route = p,
                    Icon = new LucideIcon { Kind = p.Icon, Size = 20 },
                    [!NavigationBarItem.ToolTipProperty] = p.Title.ToBinding()
                }));
        CurrentItem = _itemsSource.Items.FirstOrDefault();

        ShowOobeDialogOnDemand();

        await base.ViewLoaded(cancellationToken);
    }

    /// <summary>
    /// Shows the OOBE dialog if the application is launched for the first time or after an update.
    /// </summary>
    private void ShowOobeDialogOnDemand()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (!Version.TryParse(PersistentState.PreviousLaunchVersion, out var previousLaunchVersion)) previousLaunchVersion = null;
        if (_settings.Model.CustomAssistants.Count == 0)
        {
            DialogManager
                .CreateCustomDialog(_serviceProvider.GetRequiredService<WelcomeView>())
                .ShowAsync();
        }
        else if (previousLaunchVersion != version)
        {
            DialogManager
                .CreateCustomDialog(_serviceProvider.GetRequiredService<ChangeLogView>())
                .Dismissible()
                .ShowAsync();
        }

        PersistentState.PreviousLaunchVersion = version?.ToString();
    }

    protected internal override Task ViewUnloaded()
    {
        ShowHideToTrayNotificationOnDemand();

        return base.ViewUnloaded();
    }

    private void ShowHideToTrayNotificationOnDemand()
    {
        if (PersistentState.IsHideToTrayIconNotificationShown) return;

        ServiceLocator.Resolve<INativeHelper>().ShowDesktopNotificationAsync(LocaleResolver.MainView_EverywhereHasMinimizedToTray);
        PersistentState.IsHideToTrayIconNotificationShown = true;
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}